using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class EmoteOwnershipTests
{
    private static readonly GameCodec Wire = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ownership_combines_starting_saved_and_trusted_administrator_grants_without_filtering_unknown_saved_ids(bool administrator)
    {
        int[] saved = [81_003, 81_001, 81_003, int.MaxValue];
        int[] grants = administrator ? [91_002, 91_001, 81_003] : [];
        var owned = EmoteOwnershipService.Owned(saved, grants);
        int[] expected = administrator
            ? [1, 97, 98, 127, 81_001, 81_003, 91_001, 91_002, int.MaxValue]
            : [1, 97, 98, 127, 81_001, 81_003, int.MaxValue];
        Assert.Equal(expected, EmoteOwnershipService.Snapshot(owned).EmotesId);
        Assert.Equal(new[] { 81_003, 81_001, 81_003, int.MaxValue }, saved);
        Assert.Equal(new[] { 1, 97, 98, 127 }, EmoteOwnershipService.Snapshot(EmoteOwnershipService.Owned([], [])).EmotesId);
    }

    [Fact]
    public async Task Snapshots_use_the_owning_session_codec_and_preserve_empty_and_detached_sorted_collections()
    {
        var first = Recipient(71_001);
        var second = Recipient(71_002);
        first.Session.State.Emotes.UnionWith([81_003, 81_001, 81_003, int.MaxValue]);
        var detached = EmoteOwnershipService.Snapshot(first.Session.State.Emotes);
        first.Session.State.Emotes.Add(81_002);
        Assert.Equal(new[] { 81_001, 81_003, int.MaxValue }, detached.EmotesId);
        using (SessionContext.Push(second.Session)) await Emotes.SendListAsync(first.Session);
        using (SessionContext.Push(first.Session)) await Emotes.SendListAsync(second.Session);
        Assert.Equal(new[] { 81_001, 81_002, 81_003, int.MaxValue },
            Assert.IsType<EmotesEvent>(Assert.Single(first.Frames).Content).EmotesId);
        Assert.Empty(Assert.IsType<EmotesEvent>(Assert.Single(second.Frames).Content).EmotesId);
        foreach (var target in new[] { first, second })
        {
            Assert.Equal(1, target.Codec.Encodes);
            Assert.Equal(0, target.Codec.Decodes);
            Assert.Null(Assert.Single(target.Frames).Uid);
        }
    }

    [Fact]
    public async Task Learning_saves_for_the_owner_before_granting_and_notifying_and_never_learns_twice()
    {
        var backend = new Backend();
        var service = new EmoteOwnershipService(backend);
        var ambient = Recipient(71_002);
        var calls = backend.Calls;
        var target = Recipient(71_001, frame =>
        {
            Assert.Equal(new EmoteAddedEvent(81_001), frame.Content);
            Assert.Null(frame.Uid);
            calls.Add("notify");
        });
        backend.BeforeSave = (id, emote) =>
        {
            Assert.Equal(71_001, id);
            Assert.Equal(81_001, emote);
            Assert.DoesNotContain(emote, target.Session.State.Emotes);
            Assert.Empty(target.Frames);
        };
        using (SessionContext.Push(ambient.Session))
        using (await target.Session.EnterTurnAsync())
        {
            Assert.True(await service.LearnAsync(target.Session, 81_001).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(ambient.Session, SessionContext.Actual);
            Assert.False(await service.LearnAsync(target.Session, 81_001));
        }
        Assert.Equal(new[] { "exists:81001", "save:71001:81001", "notify", "exists:81001" }, calls);
        Assert.Equal(new[] { (71_001L, 81_001) }, backend.Saved);
        Assert.Equal(new[] { 81_001 }, target.Session.State.Emotes);
        Assert.Empty(ambient.Session.State.Emotes);
        Assert.Empty(ambient.Frames);
        Assert.Equal(1, target.Codec.Encodes);
        Assert.Equal(0, target.Codec.Decodes);
    }

    [Fact]
    public async Task Unknown_emotes_are_refused_without_persistence_mutation_or_notification()
    {
        var target = Recipient(71_001);
        var backend = new Backend();
        Assert.False(await new EmoteOwnershipService(backend).LearnAsync(target.Session, 91_001));
        Assert.Empty(target.Session.State.Emotes);
        Assert.Empty(backend.Saved);
        Assert.Empty(target.Frames);
        Assert.Equal(0, target.Codec.Encodes);
    }

    [Fact]
    public async Task Persistence_failure_leaves_ownership_unchanged_and_sends_nothing()
    {
        var failure = new InvalidOperationException("emote save failure");
        var backend = new Backend { BeforeSave = (_, _) => throw failure };
        var target = Recipient(71_001);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new EmoteOwnershipService(backend).LearnAsync(target.Session, 81_001)));
        Assert.Empty(target.Session.State.Emotes);
        Assert.Empty(backend.Saved);
        Assert.Empty(target.Frames);
        Assert.Equal(0, target.Codec.Encodes);
    }

    [Fact]
    public async Task Failed_learning_notification_preserves_saved_ownership_without_retrying_or_respending()
    {
        var failure = new IOException("emote transport failure");
        var backend = new Backend();
        var target = Recipient(71_001, _ => throw failure);
        var service = new EmoteOwnershipService(backend);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => service.LearnAsync(target.Session, 81_001)));
        Assert.Contains(81_001, target.Session.State.Emotes);
        Assert.Equal(new[] { (71_001L, 81_001) }, backend.Saved);
        Assert.False(await service.LearnAsync(target.Session, 81_001));
        Assert.Equal(1, target.Codec.Encodes);
    }

    [Fact]
    public async Task Codec_failure_after_persistence_preserves_ownership_and_never_reaches_transport()
    {
        var failure = new GameConversionException("emote codec failure");
        int sends = 0;
        var session = new GameSession(new FailingCodec(failure), _ => { sends++; return Task.CompletedTask; });
        session.State.CharacterId = 71_001;
        var backend = new Backend();
        Assert.Same(failure, await Assert.ThrowsAsync<GameConversionException>(() =>
            new EmoteOwnershipService(backend).LearnAsync(session, 81_001)));
        Assert.Contains(81_001, session.State.Emotes);
        Assert.Single(backend.Saved);
        Assert.Equal(0, sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_map_hook_sequence_sends_snapshot_between_journal_and_achievements_under_the_existing_turn(bool empty)
    {
        var calls = new List<string>();
        var target = Recipient(71_001, frame =>
        {
            calls.Add("emotes");
            Assert.IsType<EmotesEvent>(frame.Content);
        });
        target.Session.State.Emotes.UnionWith(empty ? [] : new[] { 81_003, 81_001 });
        var backend = new MapBackend(target.Session, calls);
        var ambient = Recipient(71_002);
        using (SessionContext.Push(ambient.Session))
        using (await target.Session.EnterTurnAsync())
        {
            await new GameEntryMapInitialization(backend).SendAsync(target.Session, new() { Id = 71_001 })
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(ambient.Session, SessionContext.Actual);
        }
        Assert.Equal(new[] { "map", "wardrobe", "houses", "journal", "emotes", "achievements", "presets", "marks" }, calls);
        Assert.Equal(empty ? [] : new[] { 81_001, 81_003 }, Assert.IsType<EmotesEvent>(Assert.Single(target.Frames).Content).EmotesId);
        Assert.Empty(ambient.Frames);
        Assert.Equal(1, target.Codec.Encodes);
    }

    [Fact]
    public async Task Snapshot_transport_failure_stops_later_map_hooks_and_restores_context()
    {
        var failure = new IOException("snapshot transport failure");
        var calls = new List<string>();
        var target = Recipient(71_001, _ => throw failure);
        var ambient = Recipient(71_002);
        using (SessionContext.Push(ambient.Session))
        {
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() =>
                new GameEntryMapInitialization(new MapBackend(target.Session, calls)).SendAsync(target.Session, new() { Id = 71_001 })));
            Assert.Same(ambient.Session, SessionContext.Actual);
        }
        Assert.Equal(new[] { "map", "wardrobe", "houses", "journal" }, calls);
        Assert.Equal(1, target.Codec.Encodes);
    }

    private static (GameSession Session, List<GameFrame> Frames, GameMessagingTests.CountingCodec Codec) Recipient(
        long id, Action<GameFrame>? onSend = null)
    {
        var frames = new List<GameFrame>();
        var codec = new GameMessagingTests.CountingCodec();
        var session = new GameSession(codec, packet =>
        {
            var frame = Wire.Decode(packet);
            onSend?.Invoke(frame);
            frames.Add(frame);
            return Task.CompletedTask;
        });
        session.State.CharacterId = id;
        return (session, frames, codec);
    }

    private sealed class Backend : IEmoteOwnershipBackend
    {
        internal List<string> Calls { get; } = [];
        internal List<(long, int)> Saved { get; } = [];
        internal Action<long, int>? BeforeSave;
        public bool Exists(int emoteId) { Calls.Add($"exists:{emoteId}"); return emoteId == 81_001; }
        public void Save(long characterId, int emoteId)
        {
            Calls.Add($"save:{characterId}:{emoteId}");
            BeforeSave?.Invoke(characterId, emoteId);
            Saved.Add((characterId, emoteId));
        }
    }

    private sealed class MapBackend(GameSession owner, List<string> calls) : IGameEntryMapBackend
    {
        private Task Record(string call, GameSession session)
        {
            Assert.Same(owner, session);
            Assert.Same(owner, SessionContext.Actual);
            calls.Add(call);
            return Task.CompletedTask;
        }
        public Task SendMapAsync(GameSession session, DatabaseManager.DbCharacter character)
        {
            Assert.Equal(owner.CharacterId, character.Id);
            return Record("map", session);
        }
        public Task SendWardrobeAsync(GameSession session) => Record("wardrobe", session);
        public Task SendHousesAsync(GameSession session) => Record("houses", session);
        public Task SendJournalAsync(GameSession session) => Record("journal", session);
        public Task SendAchievementsAsync(GameSession session) => Record("achievements", session);
        public Task SendPresetsAsync(GameSession session) => Record("presets", session);
        public Task SendMarksAsync(GameSession session) => Record("marks", session);
    }

    private sealed class FailingCodec(GameConversionException failure) : IGameCodec
    {
        public GameFrame Decode(ReadOnlySpan<byte> payload) => throw new InvalidOperationException("No decode expected.");
        public byte[] Encode(GameFrame message) => throw failure;
    }
}
