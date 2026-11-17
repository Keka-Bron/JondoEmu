using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class MapPresenceServiceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(6)]
    public async Task Travel_notifies_captured_old_map_then_followers_then_new_map_excluding_the_mover(int? direction)
    {
        const long mapId = 91_000_001;
        var ownCodec = new Codec();
        var mateCodec = new Codec();
        var strangerCodec = new Codec();
        using var own = new GameMessagingTests.Recipient(mapId, ownCodec);
        using var mate = new GameMessagingTests.Recipient(mapId, mateCodec);
        using var stranger = new GameMessagingTests.Recipient(mapId, strangerCodec);
        using var destination = new GameMessagingTests.Recipient(mapId + 1, new Codec());
        using var elsewhere = new GameMessagingTests.Recipient(mapId + 2, new Codec());
        var oldMap = own.Session.Map;
        var backend = new Backend(own.Session, mate.Session.CharacterId);
        backend.OnFollow = () =>
        {
            Assert.IsType<ContextRemoveElementEvent>(mateCodec.Frames[^1].Content);
            Assert.IsType<ContextRemoveElementEvent>(strangerCodec.Frames[^1].Content);
            Assert.Empty(destination.Packets);
        };
        var service = new MapPresenceService(backend);
        own.Session.State.MapId++;
        own.Session.State.CellId = 312;
        using (SessionContext.Push(elsewhere.Session))
        using (await own.Session.EnterTurnAsync())
        using (await mate.Session.EnterTurnAsync())
        {
            Assert.Equal((2, 1), await service.TravelAsync(own.Session, oldMap, direction).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(elsewhere.Session, SessionContext.Actual);
        }
        Assert.Equal(new[] { "follow", "find", "actor" }, backend.Calls);
        if (direction.HasValue)
        {
            Assert.Equal(new MapChangeOrientationEvent(own.Session.CharacterId, direction == 0 ? Direction.East : Direction.North), mateCodec.Frames[0].Content);
            Assert.Equal(2, mateCodec.Frames.Count);
        }
        else Assert.Single(mateCodec.Frames);
        Assert.Equal(new ContextRemoveElementEvent(own.Session.CharacterId), Assert.Single(strangerCodec.Frames).Content);
        var arrival = Assert.IsType<GameRolePlayShowActorsEvent>(Assert.Single(ownCodec.Frames).Content);
        Assert.Equal(312, Assert.Single(arrival.Actors).Disposition.CellId);
        Assert.Single(destination.Packets);
        Assert.Empty(own.Packets);
        Assert.Empty(elsewhere.Packets);
        Assert.Equal(0, ownCodec.Decodes + mateCodec.Decodes + strangerCodec.Decodes);
        Assert.All(mateCodec.Frames.Concat(strangerCodec.Frames).Concat(ownCodec.Frames), frame => Assert.Null(frame.Uid));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Departure_codec_or_transport_failure_is_isolated_to_the_recipient_and_keeps_registration(bool transport)
    {
        const long mapId = 91_000_011;
        var failure = new IOException("recipient failed");
        var brokenCodec = new Codec { Failure = transport ? null : failure };
        var goodCodec = new Codec();
        using var own = new GameMessagingTests.Recipient(mapId, new Codec());
        using var broken = new GameMessagingTests.Recipient(mapId, brokenCodec,
            send: transport ? _ => Task.FromException(failure) : null);
        using var good = new GameMessagingTests.Recipient(mapId, goodCodec);
        var service = new MapPresenceService(new Backend(own.Session, broken.Session.CharacterId));
        Assert.Equal(1, await service.RemoveAsync(own.Session.Map, own.Session.CharacterId, own.Session.Id, 4));
        Assert.IsType<MapChangeOrientationEvent>(Assert.Single(brokenCodec.Frames).Content);
        Assert.IsType<ContextRemoveElementEvent>(Assert.Single(goodCodec.Frames).Content);
        Assert.True(SessionRegistry.TryGet(broken.Session.Id, out _));
        Assert.Empty(own.Packets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Presentation_refresh_preserves_own_then_map_delivery_and_optional_broadcast(bool includeMap)
    {
        const long mapId = 91_000_021;
        var codec = new Codec();
        var peerCodec = new Codec();
        using var own = new GameMessagingTests.Recipient(mapId, codec);
        using var peer = new GameMessagingTests.Recipient(mapId, peerCodec);
        var backend = new Backend(own.Session, 0);
        await new MapPresenceService(backend).RefreshAsync(own.Session, backend.Character, includeMap);
        Assert.Single(own.Packets);
        Assert.Equal(includeMap ? 1 : 0, peer.Packets.Count);
        Assert.Single(codec.Frames);
        Assert.Equal(includeMap ? 1 : 0, peerCodec.Frames.Count);
        Assert.All(peerCodec.Frames, frame => Assert.IsType<GameRolePlayShowActorsEvent>(frame.Content));
        Assert.All(codec.Frames, frame => Assert.IsType<GameRolePlayShowActorsEvent>(frame.Content));
        Assert.Equal(new[] { "actor" }, backend.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Presentation_recipient_failures_are_isolated_without_taking_recipient_turns(bool transport)
    {
        const long mapId = 91_000_025;
        var failure = new IOException("peer refresh failed");
        var brokenCodec = new Codec { Failure = transport ? null : failure };
        var goodCodec = new Codec();
        using var own = new GameMessagingTests.Recipient(mapId, new Codec());
        using var broken = new GameMessagingTests.Recipient(mapId, brokenCodec,
            send: transport ? _ => Task.FromException(failure) : null);
        using var good = new GameMessagingTests.Recipient(mapId, goodCodec);
        var backend = new Backend(own.Session, 0);
        using var turn = await good.Session.EnterTurnAsync();
        await new MapPresenceService(backend).RefreshAsync(own.Session, backend.Character).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(own.Packets);
        Assert.Single(brokenCodec.Frames);
        Assert.IsType<GameRolePlayShowActorsEvent>(Assert.Single(goodCodec.Frames).Content);
        Assert.Single(good.Packets);
        Assert.True(SessionRegistry.TryGet(broken.Session.Id, out _));
        Assert.Equal(0, good.Session.UnoCadaVez.CurrentCount);
    }

    [Fact]
    public async Task Refresh_source_and_own_delivery_failures_propagate_before_broadcast()
    {
        const long mapId = 91_000_031;
        var failure = new IOException("own refresh failed");
        var codec = new Codec();
        using var own = new GameMessagingTests.Recipient(mapId, codec, send: _ => Task.FromException(failure));
        using var peer = new GameMessagingTests.Recipient(mapId);
        var backend = new Backend(own.Session, 0) { Failure = failure };
        var service = new MapPresenceService(backend);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => service.RefreshAsync(own.Session, backend.Character)));
        Assert.Empty(codec.Frames);
        backend.Failure = null;
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => service.RefreshAsync(own.Session, backend.Character)));
        Assert.Single(codec.Frames);
        Assert.Empty(peer.Packets);
    }

    [Fact]
    public void Map_snapshot_excludes_fighters_spectators_inactive_sessions_and_other_maps_without_using_ambient_owner()
    {
        const long mapId = 91_000_041;
        using var own = new GameMessagingTests.Recipient(mapId);
        using var peer = new GameMessagingTests.Recipient(mapId);
        using var fighter = new GameMessagingTests.Recipient(mapId);
        using var spectator = new GameMessagingTests.Recipient(mapId);
        using var inactive = new GameMessagingTests.Recipient(mapId, enterWorld: false);
        using var elsewhere = new GameMessagingTests.Recipient(mapId + 1);
        fighter.Session.State.IsInFight = true;
        spectator.Session.State.SpectatingFightId = 123;
        using (SessionContext.Push(elsewhere.Session))
            Assert.Same(peer.Session, Assert.Single(MapInformationMessages.OtherPlayers(own.Session, own.Session.CharacterId)));
    }

    private sealed class Backend(GameSession owner, long mate) : IMapPresenceBackend
    {
        internal DbCharacter Character = new() { Id = owner.CharacterId, Name = "Traveler" };
        internal List<string> Calls = [];
        internal Exception? Failure;
        internal Action? OnFollow;
        private readonly Parties.Party _party = new();
        public DbCharacter? FindCharacter(long id) { Assert.Equal(owner.CharacterId, id); Calls.Add("find"); return Character; }
        public GameRolePlayShowActorsEvent Refreshed(GameSession session, DbCharacter character)
        {
            Assert.Same(owner, session);
            Assert.Same(owner, SessionContext.Actual);
            Assert.Same(Character, character);
            Calls.Add("actor");
            if (Failure != null) throw Failure;
            return new([MapActorMessages.Player(character, session.State.CellId, session.State.Orientation, session.AccountId,
                new([], 3, 1, [], [], []), new(0, Alignment.Neutral, null, 0, 200), null, [])]);
        }
        public Parties.Party? PartyOf(long id) => id == owner.CharacterId || id == mate ? _party : null;
        public Task LeaderMovedAsync(GameSession session) { Calls.Add("follow"); OnFollow?.Invoke(); return Task.CompletedTask; }
    }

    private sealed class Codec : IGameCodec
    {
        internal List<GameFrame> Frames = [];
        internal Exception? Failure;
        internal int Decodes;
        public byte[] Encode(GameFrame frame) { Frames.Add(frame); if (Failure != null) throw Failure; return [0x71]; }
        public GameFrame Decode(ReadOnlySpan<byte> payload) { Decodes++; throw new InvalidOperationException("Unexpected presence decode"); }
    }
}
