using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class GameEntryMapMessagesTests
{
    internal static SpellsEvent EmptySpells(GameSession _) => new([], []);
    internal static (ShortcutBarContentEvent Spell, ShortcutBarContentEvent General) EmptyBars(GameSession _)
        => (new([], ShortcutBar.Spell), new([], ShortcutBar.General));
    internal static GameEntryMapMessages Default { get; } = new(TimeProvider.System, () => 5, EmptySpells, EmptyBars);
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_794_456_789_123);
    private static readonly DateTime Earlier = Now.AddHours(-1).UtcDateTime;
    private const long MapId = 9_000_000_001;

    [Theory]
    [InlineData(5)]
    [InlineData(1)]
    public async Task Roleplay_sends_current_environment_then_spells_community_channels_and_the_empty_key(int rate)
    {
        var sequence = new List<object>();
        var own = new Recipient(send: frame => { sequence.Add(frame.Content); return Task.CompletedTask; });
        own.Session.State.MapId = 81_002; // The resolved entry map, not this earlier state, is authoritative.
        int reads = 0, snapshots = 0;
        var spells = new SpellsEvent([new(3, 71_001, SpellType.Unknown1)], []);
        var service = new GameEntryMapMessages(new Clock(() => Now), () => { reads++; return rate; }, session =>
        {
            Assert.Same(own.Session, session);
            snapshots++;
            return spells;
        }, EmptyBars);

        int sent = await service.SendAsync(own.Session, MapEntryOpeningMessagesTests.Empty, GameContext.RolePlay, MapId);

        Assert.Equal(34, sent);
        Assert.Equal(new LifePointsRegenBeginEvent(rate), sequence[5]);
        Assert.Empty(Assert.IsType<NpcsMapQuestStatusUpdateEvent>(sequence[6]).MapInformation);
        Assert.Equal(new MapCurrentEvent(MapId), sequence[7]);
        Assert.Equal(new TimeEvent(120, 1_794_456_789_123), sequence[8]);
        Assert.Same(spells, sequence[9]);
        Assert.Equal(ShortcutBar.Spell, Assert.IsType<ShortcutBarContentEvent>(sequence[10]).BarType);
        Assert.Equal(ShortcutBar.General, Assert.IsType<ShortcutBarContentEvent>(sequence[11]).BarType);
        Assert.IsType<CommunityChannelEvent>(sequence[12]);
        var updates = sequence.Skip(13).Take(20).Select(Assert.IsType<ChannelUpdateEvent>).ToArray();
        Assert.Equal(20, updates.Select(update => update.Channel).Distinct().Count());
        Assert.Equal(new BakApiKeyEvent(""), sequence[33]);
        Assert.Equal(1, reads);
        Assert.Equal(1, snapshots);
        Assert.Equal(Now.UtcDateTime, own.Session.State.RegenerationStartedUtc);
        Assert.Equal(34, own.Codec.Frames.Count);
        Assert.All(own.Codec.Frames, frame => Assert.Null(frame.Uid));
        Assert.Equal(0, own.Codec.Decodes);
    }

    [Fact]
    public async Task Reconnection_omits_roleplay_resets_and_runs_its_counted_hook_immediately_after_time()
    {
        var own = new Recipient();
        own.Session.State.RegenerationStartedUtc = Earlier;
        own.Session.State.IsInFight = false; // The explicit resolved context owns the decision.
        var service = new GameEntryMapMessages(new Clock(() => Now), () => throw new Exception("Fight read regeneration"), EmptySpells, EmptyBars);
        int hooks = 0;

        int sent = await service.SendAsync(own.Session, MapEntryOpeningMessagesTests.Empty, GameContext.Fight, MapId, async () =>
        {
            hooks++;
            Assert.Equal(new MapCurrentEvent(MapId), own.Codec.Frames[^2].Content);
            Assert.Equal(new TimeEvent(120, 1_794_456_789_123), own.Codec.Frames[^1].Content);
            await own.Session.SendAsync(new TextInformationEvent(TextInformationType.TextInformationMessage, 71_001, ["Returning"]));
            await own.Session.SendAsync(new AreaFightModifierUpdateEvent([]));
            return 2;
        });

        Assert.Equal(34, sent);
        Assert.Equal(1, hooks);
        Assert.DoesNotContain(own.Codec.Frames, frame => frame.Content is LifePointsRegenBeginEvent or NpcsMapQuestStatusUpdateEvent);
        Assert.Equal(new ContextCreationEvent(GameContext.Fight), own.Codec.Frames[4].Content);
        Assert.IsType<SpellsEvent>(own.Codec.Frames[9].Content);
        Assert.Equal(ShortcutBar.Spell, Assert.IsType<ShortcutBarContentEvent>(own.Codec.Frames[10].Content).BarType);
        Assert.Equal(ShortcutBar.General, Assert.IsType<ShortcutBarContentEvent>(own.Codec.Frames[11].Content).BarType);
        Assert.IsType<CommunityChannelEvent>(own.Codec.Frames[12].Content);
        Assert.Equal(new BakApiKeyEvent(""), own.Codec.Frames[^1].Content);
        Assert.Equal(Earlier, own.Session.State.RegenerationStartedUtc);
        Assert.Equal(34, own.Codec.Frames.Count);
    }

    [Fact]
    public async Task Explicit_owner_supplies_channels_and_regeneration_state_and_keeps_the_callers_turn_and_context()
    {
        var own = new Recipient();
        var other = new Recipient();
        own.Session.State.ActiveChatChannels = [ChatChannel.Private, ChatChannel.Unknown17];
        other.Session.State.ActiveChatChannels = [ChatChannel.General];
        other.Session.State.RegenerationStartedUtc = Earlier;
        using var scope = SessionContext.Push(other.Session);
        using var turn = await own.Session.EnterTurnAsync();

        await new GameEntryMapMessages(new Clock(() => Now), () => 5, EmptySpells, EmptyBars).SendAsync(own.Session,
            MapEntryOpeningMessagesTests.Empty, GameContext.RolePlay, MapId)
            .WaitAsync(TimeSpan.FromSeconds(5));

        var enabled = own.Codec.Frames.Select(frame => frame.Content).OfType<ChannelUpdateEvent>().Where(update => update.Enable);
        Assert.Equal(new[] { ChatChannel.Private, ChatChannel.Unknown17 }, enabled.Select(update => update.Channel));
        Assert.Equal(Now.UtcDateTime, own.Session.State.RegenerationStartedUtc);
        Assert.Equal(Earlier, other.Session.State.RegenerationStartedUtc);
        Assert.Empty(other.Codec.Frames);
        Assert.Same(other.Session, SessionContext.Actual);
        Assert.Equal(0, own.Session.UnoCadaVez.CurrentCount);
        Assert.Equal(0, own.Codec.Decodes);
    }

    [Fact]
    public async Task Time_is_refreshed_on_each_entry_and_uses_the_shared_welcome_policy()
    {
        var now = Now;
        var clock = new Clock(() => now);
        var service = new GameEntryMapMessages(clock, () => 5, EmptySpells, EmptyBars);
        var own = new Recipient();
        await service.SendAsync(own.Session,
            MapEntryOpeningMessagesTests.Empty, GameContext.RolePlay, MapId);
        now = now.AddMilliseconds(123_456);
        await service.SendAsync(own.Session,
            MapEntryOpeningMessagesTests.Empty, GameContext.RolePlay, MapId + 1);

        Assert.Equal(new[] { new TimeEvent(120, 1_794_456_789_123), new TimeEvent(120, 1_794_456_912_579) },
            own.Codec.Frames.Select(frame => frame.Content).OfType<TimeEvent>());
        Assert.Equal(new TimeEvent(120, 1_794_456_912_579), ServerTimeMessages.Snapshot(clock));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Invalid_map_fails_before_reading_sources_or_sending(long mapId)
    {
        var own = new Recipient();
        var service = new GameEntryMapMessages(new Clock(() => throw new Exception("Unexpected clock")),
            () => throw new Exception("Unexpected rate"), _ => throw new Exception("Unexpected spells"), _ => throw new Exception("Unexpected shortcuts"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SendAsync(own.Session,
            MapEntryOpeningMessagesTests.Empty, GameContext.RolePlay, mapId));
        Assert.Empty(own.Codec.Frames);
    }

    [Theory]
    [InlineData("regeneration", 5)]
    [InlineData("clock", 8)]
    [InlineData("spells", 9)]
    [InlineData("shortcuts", 10)]
    public async Task Source_failures_propagate_without_sending_later_messages(string stage, int attempted)
    {
        var failure = new IOException("map source failed");
        var own = new Recipient();
        own.Session.State.RegenerationStartedUtc = Earlier;
        var service = new GameEntryMapMessages(new Clock(() => stage == "clock" ? throw failure : Now),
            () => stage == "regeneration" ? throw failure : 5, session => stage == "spells" ? throw failure : EmptySpells(session),
            session => stage == "shortcuts" ? throw failure : EmptyBars(session));

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => service.SendAsync(own.Session, MapEntryOpeningMessagesTests.Empty,
            GameContext.RolePlay, MapId)));

        Assert.Equal(attempted, own.Codec.Frames.Count);
        Assert.DoesNotContain(own.Codec.Frames, frame => frame.Content is ShortcutBarContentEvent or CommunityChannelEvent or BakApiKeyEvent);
        Assert.Equal(stage == "shortcuts" ? 1 : 0, own.Codec.Frames.Count(frame => frame.Content is SpellsEvent));
        Assert.Equal(stage is "spells" or "shortcuts" ? 1 : 0, own.Codec.Frames.Count(frame => frame.Content is TimeEvent));
        Assert.Equal(Earlier, own.Session.State.RegenerationStartedUtc);
    }

    public static IEnumerable<object[]> DeliveryFailures()
        => new[] { "regeneration", "marks", "map", "time", "spells", "spell-bar", "general-bar", "community", "channel", "key" }
            .SelectMany(stage => new[] { new object[] { stage, false }, new object[] { stage, true } });

    [Theory]
    [MemberData(nameof(DeliveryFailures))]
    public async Task Codec_and_transport_failures_stop_at_the_failed_event_without_retry(string stage, bool transport)
    {
        bool IsFailed(GameFrame frame) => stage switch
        {
            "regeneration" => frame.Content is LifePointsRegenBeginEvent,
            "marks" => frame.Content is NpcsMapQuestStatusUpdateEvent,
            "map" => frame.Content is MapCurrentEvent,
            "time" => frame.Content is TimeEvent,
            "spells" => frame.Content is SpellsEvent,
            "spell-bar" => frame.Content is ShortcutBarContentEvent { BarType: ShortcutBar.Spell },
            "general-bar" => frame.Content is ShortcutBarContentEvent { BarType: ShortcutBar.General },
            "community" => frame.Content is CommunityChannelEvent,
            "channel" => frame.Content is ChannelUpdateEvent { Channel: ChatChannel.General },
            _ => frame.Content is BakApiKeyEvent,
        };
        var failure = new IOException("map delivery failed");
        int sends = 0;
        var own = new Recipient(frame => !transport && IsFailed(frame) ? failure : null, frame =>
        {
            sends++;
            return transport && IsFailed(frame) ? Task.FromException(failure) : Task.CompletedTask;
        });
        own.Session.State.RegenerationStartedUtc = Earlier;
        var other = new Recipient();
        using var scope = SessionContext.Push(other.Session);

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => new GameEntryMapMessages(new Clock(() => Now), () => 5, EmptySpells, EmptyBars)
            .SendAsync(own.Session,
                MapEntryOpeningMessagesTests.Empty, GameContext.RolePlay, MapId)));

        Assert.True(IsFailed(own.Codec.Frames[^1]));
        Assert.Single(own.Codec.Frames, IsFailed);
        Assert.Equal(own.Codec.Frames.Count - (transport ? 0 : 1), sends);
        Assert.Equal(own.Codec.Frames.Count - 1, own.Delivered.Count);
        Assert.Equal(Earlier, own.Session.State.RegenerationStartedUtc);
        Assert.Equal(0, own.Codec.Decodes);
        Assert.Empty(other.Codec.Frames);
        Assert.Same(other.Session, SessionContext.Actual);
    }

    [Fact]
    public async Task Reconnection_hook_failure_stops_before_spells_without_retry()
    {
        var failure = new IOException("map continuation failed");
        var own = new Recipient();
        int hooks = 0;
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => Default.SendAsync(own.Session,
            MapEntryOpeningMessagesTests.Empty, GameContext.Fight, MapId,
            () => { hooks++; return Task.FromException<int>(failure); })));

        Assert.Equal(1, hooks);
        Assert.DoesNotContain(own.Codec.Frames, frame => frame.Content is SpellsEvent or ShortcutBarContentEvent);
        Assert.DoesNotContain(own.Codec.Frames, frame => frame.Content is CommunityChannelEvent or ChannelUpdateEvent or BakApiKeyEvent);
        Assert.Equal(default, own.Session.State.RegenerationStartedUtc);
    }

    private sealed class Clock(Func<DateTimeOffset> now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now();
    }

    private sealed class Recipient
    {
        internal RecordingCodec Codec { get; }
        internal List<GamePayload> Delivered { get; } = [];
        internal GameSession Session { get; }
        internal Recipient(Func<GameFrame, Exception?>? failure = null, Func<GameFrame, Task>? send = null)
        {
            Codec = new(failure);
            Session = new(Codec, async _ =>
            {
                var frame = Codec.Frames[^1];
                if (send != null) await send(frame);
                Delivered.Add(frame.Content);
            });
        }
    }

    private sealed class RecordingCodec(Func<GameFrame, Exception?>? failure) : IGameCodec
    {
        internal List<GameFrame> Frames { get; } = [];
        internal int Decodes { get; private set; }
        public byte[] Encode(GameFrame frame)
        {
            Frames.Add(frame);
            if (failure?.Invoke(frame) is { } error) throw error;
            return []; // Opaque transport token; semantic tests never serialize protocol data.
        }
        public GameFrame Decode(ReadOnlySpan<byte> payload)
        {
            Decodes++;
            throw new InvalidOperationException("Unexpected decode in semantic map entry");
        }
    }
}
