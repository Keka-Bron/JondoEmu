using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.Movement;

[Collection("MapManager")]
public sealed class MappedWorldMovementTests
{
    private const long MapId = 192937990;
    private static readonly GameCodec Codec = new();

    private static void LoadWorld()
    {
        if (MapManager.GetMapInfo(MapId) == null) MapManager.Initialize();
        using var db = new SqliteConnection(DatabaseManager.WorldConnectionString);
        db.Open();
        DatabaseManager.MoveScrollsOutOfTheBase(db);
    }

    // Independent 3.6.10.10 envelope/payload construction, including unpacked repeated
    // scalars which the old hand-written movement reader did not handle.
    private static byte[] Move(long mapId, int[] keys, bool cautious, bool unpacked)
    {
        var body = Pb.New().Var(1, mapId).VarIfNotZero(3, cautious ? 1 : 0);
        if (unpacked) foreach (int key in keys) body.Var(2, key);
        else body.Packed(2, keys.Select(k => (long)k));
        return Pb.New().Msg(2, Pb.New().Msg(1, Pb.New().Str(1, "type.ankama.com/jrw").Msg(2, body)).Var(2, -3)).Build();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Walk_broadcasts_typed_cells_to_the_map_then_notifies_followers_on_other_maps(bool cautious, bool unpacked)
    {
        LoadWorld();
        var order = new ConcurrentQueue<string>();
        using var mover = new Peer(MapId, _ => order.Enqueue("movement"));
        using var observer = new Peer(MapId, _ => order.Enqueue("movement"));
        using var follower = new Peer(MapId + 1, _ => order.Enqueue("follow"));
        var party = Parties.Create(mover.Session.CharacterId);
        try
        {
            Assert.True(Parties.Invite(party, follower.Session.CharacterId, mover.Session.CharacterId));
            Assert.True(Parties.Accept(party, follower.Session.CharacterId));
            Assert.NotNull(Parties.Follow(follower.Session.CharacterId, out _));
            mover.Session.State.CellId = 100;
            byte[] bytes = Move(MapId, [(3 << 12) | 100, (5 << 12) | 114], cautious, unpacked);
            using (SessionContext.Push(observer.Session))
            {
                using var turn = await mover.Session.EnterTurnAsync();
                Assert.True(await WorldMovementDispatch.TryHandleAsync(mover.Session, bytes, "type.ankama.com/jrw"));
                Assert.Same(observer.Session, SessionContext.Actual);
            }
            Assert.Equal(114, mover.Session.State.CellId);
            Assert.Equal(5, mover.Session.State.Orientation);
            Assert.Equal(MapId, mover.Session.State.PendingMovementMapId);
            Assert.Equal(114, mover.Session.State.PendingMovementCellId);
            Assert.Equal(new[] { "movement", "movement", "follow" }, order.ToArray());
            byte[] emitted = Assert.Single(mover.Packets);
            Assert.Same(emitted, Assert.Single(observer.Packets));
            var movement = Assert.IsType<GameMapMovementEvent>(Codec.Decode(emitted).Content);
            Assert.Equal(new[] { 100, 114 }, movement.Cells);
            Assert.Equal(mover.Session.CharacterId, movement.CharacterId);
            Assert.Equal(cautious, movement.Cautious);
            // The old wire-only facing field is deliberately not invented as a stable member.
            Assert.DoesNotContain(ProtoMessage.Parse(Codec.ReadEnvelope(emitted).Content.Value.ToByteArray()).Fields,
                f => f.FieldNumber == 2);
            Assert.Equal("ikv", Codec.ReadEnvelope(Assert.Single(follower.Packets)).Alias);
        }
        finally { Parties.Dissolve(party); }
    }

    [Fact]
    public async Task Cancellation_and_confirmation_clear_the_right_sessions_pending_movement()
    {
        LoadWorld();
        using var mover = new Peer(MapId);
        using var other = new Peer(MapId);
        int stop = MapManager.WalkableCells[MapId].First(c => c != 414);
        mover.Session.State.CellId = 414;
        mover.Session.State.PendingMovementMapId = MapId;
        mover.Session.State.PendingMovementCellId = 414;
        other.Session.State.PendingMovementMapId = MapId;
        other.Session.State.PendingMovementCellId = 88;
        using (SessionContext.Push(other.Session))
        {
            var bad = Codec.Encode(new(new MapMovementCancelRequest(9999), 1));
            Assert.True(await WorldMovementDispatch.TryHandleAsync(mover.Session, bad, "type.ankama.com/jqe"));
            Assert.Equal(414, mover.Session.State.CellId);
            var cancel = Codec.Encode(new(new MapMovementCancelRequest(stop), 2));
            Assert.True(await WorldMovementDispatch.TryHandleAsync(mover.Session, cancel, "type.ankama.com/jqe"));
            Assert.Equal(stop, mover.Session.State.CellId);
            Assert.False(WorldMoveHandler.TryTakeCompletedMovement(mover.Session.State, out _, out _));
            Assert.Empty(mover.Packets);

            var confirm = Codec.Encode(new(new MapMovementConfirmRequest(), -12));
            Assert.True(await WorldMovementDispatch.TryHandleAsync(mover.Session, confirm, "type.ankama.com/jqi"));
            Assert.Equal(-12, Codec.Decode(Assert.Single(mover.Packets)).Uid);
            Assert.Equal(MapId, other.Session.State.PendingMovementMapId);
            Assert.Equal(88, other.Session.State.PendingMovementCellId);
        }
        Assert.Empty(other.Packets);
    }

    [Fact]
    public async Task Cell_zero_and_invalid_facing_preserve_proto3_and_existing_orientation_semantics()
    {
        LoadWorld();
        using var mover = new Peer(MapId);
        mover.Session.State.Orientation = 6;
        await WorldMoveHandler.ConfirmMovementAsync(mover.Session, new(MapId, [8 << 12], false));
        Assert.Equal(0, mover.Session.State.CellId);
        Assert.Equal(6, mover.Session.State.Orientation);
        Assert.Equal(0, mover.Session.State.PendingMovementCellId);
        Assert.Equal(new[] { 0 }, Assert.IsType<GameMapMovementEvent>(Codec.Decode(Assert.Single(mover.Packets)).Content).Cells);
    }

    [Fact]
    public async Task Haven_bag_audience_is_bound_to_the_owner_not_the_ambient_session()
    {
        LoadWorld();
        Merkasako.Initialize();
        Assert.NotEmpty(Merkasako.Themes);
        long mapId = Merkasako.MapOfTheme(Merkasako.Themes[0]);
        using var host = new Peer(mapId);
        using var guest = new Peer(mapId);
        using var stranger = new Peer(mapId);
        guest.Session.State.HavenBagOwnerId = host.Session.CharacterId;
        var audience = host.Session.Map;
        using (SessionContext.Push(stranger.Session))
            Assert.Equal(2, await audience.SendAsync(new PongEvent(false)));
        Assert.Single(host.Packets);
        Assert.Single(guest.Packets);
        Assert.Empty(stranger.Packets);

        host.Session.State.HavenBagOwnerId = stranger.Session.CharacterId;
        Assert.Equal(1, await audience.SendAsync(new PongEvent(false)));
        Assert.Equal(2, guest.Packets.Count);
        Assert.Single(host.Packets);
        Assert.Empty(stranger.Packets);
    }

    [Fact]
    public async Task House_audience_retains_the_captured_door_and_instance()
    {
        LoadWorld();
        Houses.Initialize();
        Assert.True(Houses.TryGetDoor(212601864, 522653, out var door));
        Assert.True(door.HasCopy(2));
        using var host = new Peer(door.InteriorMapId);
        using var guest = new Peer(door.InteriorMapId);
        using var stranger = new Peer(door.InteriorMapId);
        foreach (var peer in new[] { host, guest, stranger })
        {
            peer.Session.State.HouseEntryMapId = door.MapId;
            peer.Session.State.HouseEntryElementId = door.ElementId;
            peer.Session.State.HouseEntryInstance = peer == stranger ? 2 : 1;
        }
        Assert.NotEqual(HouseHandler.HouseOf(host.Session.State), HouseHandler.HouseOf(stranger.Session.State));
        var audience = host.Session.Map;
        using (SessionContext.Push(stranger.Session))
            Assert.Equal(2, await audience.SendAsync(new PongEvent(false)));
        Assert.Single(host.Packets);
        Assert.Single(guest.Packets);
        Assert.Empty(stranger.Packets);
        host.Session.State.HouseEntryInstance = 2;
        Assert.Equal(1, await audience.SendAsync(new PongEvent(false)));
        Assert.Equal(2, guest.Packets.Count);
        Assert.Single(host.Packets);
        Assert.Empty(stranger.Packets);
    }

    private sealed class Peer : IDisposable
    {
        private static long _nextId = 91_000_000_000;
        internal ConcurrentQueue<byte[]> Packets { get; } = new();
        internal GameSession Session { get; }
        internal Peer(long mapId, Action<byte[]>? sent = null)
        {
            Session = new GameSession(Codec, bytes => { Packets.Enqueue(bytes); sent?.Invoke(bytes); return Task.CompletedTask; });
            long id = Interlocked.Increment(ref _nextId);
            Session.BindAccount(id, 1);
            Session.State.CharacterId = id;
            Session.State.MapId = mapId;
            Session.EnterWorld();
            Assert.True(SessionRegistry.Register(Session));
        }
        public void Dispose() => SessionRegistry.Unregister(Session);
    }
}
