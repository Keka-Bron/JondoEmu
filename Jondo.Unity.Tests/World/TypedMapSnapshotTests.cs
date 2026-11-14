using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Xunit;

namespace Jondo.Unity.Tests.World;

[Collection("MapManager")]
public sealed class TypedMapSnapshotTests
{
    [Fact]
    public async Task Changing_bag_owner_on_the_same_map_removes_only_from_the_captured_old_instance_and_announces_only_to_the_new_one()
    {
        Merkasako.Initialize();
        long mapId = Merkasako.MapOfTheme(Merkasako.DefaultTheme);
        Assert.NotEqual(0, mapId);
        const long oldOwner = 9_963_100_001, newOwner = 9_963_100_002, mover = 9_963_100_003;
        var delivered = new Dictionary<long, List<byte[]>>();
        var codecs = new Dictionary<long, RecordingCodec>();
        GameSession Session(long id, long bagOwner)
        {
            var codec = new RecordingCodec();
            codecs[id] = codec;
            delivered[id] = [];
            var session = new GameSession(codec, bytes => { delivered[id].Add(bytes); return Task.CompletedTask; });
            session.BindAccount(id, 1);
            session.State.CharacterId = id;
            session.State.MapId = mapId;
            session.State.HavenBagOwnerId = bagOwner;
            session.EnterWorld();
            Assert.True(SessionRegistry.Register(session));
            return session;
        }
        var source = Session(mover, oldOwner);
        var oldPeer = Session(oldOwner, 0);
        var newPeer = Session(newOwner, 0);
        var unrelated = Session(mover + 1, 0);
        try
        {
            var previous = source.Map;
            source.State.HavenBagOwnerId = newOwner;
            var backend = new PresenceBackend(source);
            using (SessionContext.Push(unrelated))
                Assert.Equal((1, 1), await new MapPresenceService(backend).TravelAsync(source, previous));
            Assert.Equal(new ContextRemoveElementEvent(mover), Assert.Single(codecs[oldOwner].Frames).Content);
            Assert.IsType<GameRolePlayShowActorsEvent>(Assert.Single(codecs[mover].Frames).Content);
            Assert.Single(delivered[oldOwner]);
            Assert.Single(delivered[newOwner]);
            Assert.Empty(delivered[mover]);
            Assert.Empty(delivered[unrelated.CharacterId]);
            Assert.Equal(0, backend.FollowUpdates);
        }
        finally
        {
            foreach (var session in new[] { source, oldPeer, newPeer, unrelated }) SessionRegistry.Unregister(session);
        }
    }

    [Fact]
    public async Task Live_actor_refresh_uses_semantic_frames_and_filters_bag_instances_and_fighting_players_on_repeated_replies()
    {
        if (!MapManager.WalkableCells.ContainsKey(Jail.MapId)) MapManager.Initialize();
        Merkasako.Initialize();
        HavenBagStore.Initialize();
        long mapId = Merkasako.MapOfTheme(Merkasako.DefaultTheme);
        Assert.NotEqual(0, mapId);
        const long account = 8_963_000_001;
        long Create(long id, string name) => DatabaseManager.CreateCharacter(id, 1, name, 1, 0, 1,
            [-1, -1, -1, -1, -1], mapId, 51, 0, 0, []);
        long ownId = Create(account, "Typed-Map-Owner");
        long peerId = Create(account + 1, "Typed-Map-Guest");
        long otherId = Create(account + 2, "Typed-Map-Other");
        Assert.True(ownId > 0 && peerId > 0 && otherId > 0);
        try
        {
            var codec = new RecordingCodec();
            await using var own = await ClientPipe.OpenAsync(account, ownId, mapId, codec: codec);
            await using var peer = await ClientPipe.OpenAsync(account + 1, peerId, mapId);
            await using var other = await ClientPipe.OpenAsync(account + 2, otherId, mapId);
            peer.Session.State.HavenBagOwnerId = ownId;
            own.Session.State.CellId = 312;
            own.Session.State.Orientation = 7;
            var character = DatabaseManager.GetCharacterById(ownId)!;
            using (SessionContext.Push(other.Session))
            using (await own.Session.EnterTurnAsync())
            {
                await MapInformationService.Live.RefreshActorsAsync(own.Session, character);
                Assert.Equal(new byte[] { 0x71, 1 }, await Read(own));
                Assert.Equal(new byte[] { 0x71, 2 }, await Read(own));
                Assert.Same(other.Session, SessionContext.Actual);
            }
            var first = Assert.IsType<MapComplementaryInformationEvent>(codec.Frames[0].Content);
            Assert.Equal(new[] { ownId, peerId }.Order(), first.Actors.Select(actor => actor.ActorId).Order());
            Assert.Equal(312, first.Actors.Single(actor => actor.ActorId == ownId).Disposition.CellId);
            var bag = Assert.IsType<MapSpecificComplementaryInformationCaseHavenBagInformation>(first.SpecificCase).Value;
            Assert.Equal(ownId, bag.OwnerInformation.Id);
            Assert.IsType<AreaFightModifierUpdateEvent>(codec.Frames[1].Content);
            peer.Session.State.IsInFight = true;
            own.Session.State.CellId = 219;
            await MapInformationService.Live.RefreshActorsAsync(own.Session, character);
            Assert.Equal(new byte[] { 0x71, 3 }, await Read(own));
            Assert.Equal(new byte[] { 0x71, 4 }, await Read(own));
            var second = Assert.IsType<MapComplementaryInformationEvent>(codec.Frames[2].Content);
            Assert.Equal(ownId, Assert.Single(second.Actors).ActorId);
            Assert.Equal(219, second.Actors[0].Disposition.CellId);
            Assert.Equal(312, first.Actors.Single(actor => actor.ActorId == ownId).Disposition.CellId);
            Assert.Equal(0, codec.Decodes);
            Assert.All(codec.Frames, frame => Assert.Null(frame.Uid));
        }
        finally
        {
            DatabaseManager.DeleteCharacter(ownId, account);
            DatabaseManager.DeleteCharacter(peerId, account + 1);
            DatabaseManager.DeleteCharacter(otherId, account + 2);
        }
    }

    private static async Task<byte[]> Read(ClientPipe pipe)
        => await Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer).WaitAsync(TimeSpan.FromSeconds(5));

    private sealed class RecordingCodec : IGameCodec
    {
        internal List<GameFrame> Frames = [];
        internal int Decodes;
        public byte[] Encode(GameFrame frame) { Frames.Add(frame); return [0x71, checked((byte)Frames.Count)]; }
        public GameFrame Decode(ReadOnlySpan<byte> payload) { Decodes++; throw new InvalidOperationException("Unexpected typed map decode"); }
    }

    private sealed class PresenceBackend(GameSession owner) : IMapPresenceBackend
    {
        internal int FollowUpdates;
        public DbCharacter? FindCharacter(long id) => new() { Id = id, Name = "Bag visitor" };
        public GameRolePlayShowActorsEvent Refreshed(GameSession session, DbCharacter character)
            => new([MapActorMessages.Player(character, 51, 1, session.AccountId, new([], 3, 1, [], [], []), null, null, [])]);
        public Parties.Party? PartyOf(long characterId) => null;
        public Task LeaderMovedAsync(GameSession session) { Assert.Same(owner, session); FollowUpdates++; return Task.CompletedTask; }
    }
}
