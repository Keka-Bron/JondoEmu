using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Movement;

[Collection("game-node-listener")]
public sealed class TypedCombatMovementTests
{
    private static readonly GameCodec Codec = new();
    private static ConcurrentDictionary<long, FightInstance> Active =>
        (ConcurrentDictionary<long, FightInstance>)typeof(FightHandler)
            .GetField("_activeFights", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    private static ConcurrentDictionary<long, (FightInstance Fight, int Team)> PendingJoins =>
        (ConcurrentDictionary<long, (FightInstance Fight, int Team)>)typeof(FightHandler)
            .GetField("_joinAfterWalk", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    // Independent wire construction exercises both protobuf representations of repeated key cells.
    private static byte[] Move(long map, IEnumerable<int> keys, bool unpacked)
    {
        var body = Pb.New().Var(1, map);
        if (unpacked) foreach (int key in keys) body.Var(2, key);
        else body.Packed(2, keys.Select(key => (long)key));
        return Request("jrw", body.Build());
    }

    private static byte[] Request(string alias, byte[] body) => Pb.New().Msg(2, Pb.New()
        .Msg(1, Pb.New().Str(1, ConnectionProtocol.UriPrefix + alias).Bytes(2, body)).Var(2, -17)).Build();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_loop_walks_once_spends_expanded_path_mp_and_accepts_packed_or_unpacked_keys(bool unpacked)
    {
        await using var pipe = await Pipe.OpenAsync();
        using var fight = new Battle(pipe.Session);
        int[] path = fight.Path;
        pipe.Start(Move(fight.Fight.MapId, [path[0] | (1 << 12), path[^1] | (1 << 12)], unpacked));
        var frames = await pipe.DrainToBarrier();

        Assert.Equal(path[^1], fight.Player.CellId);
        Assert.Equal(4, fight.Player.CurrentMP); // Three cells in a straight line cost two MP.
        Assert.Equal(1, fight.Player.LastFacing);
        byte[] moved = Assert.Single(frames, frame => ConnectionProtocol.ReadOpcode(frame) == "jsj");
        Assert.Equal(path, Assert.IsType<GameMapMovementEvent>(Codec.Decode(moved).Content).Cells);
        Assert.DoesNotContain(frames, frame => ConnectionProtocol.ReadOpcode(frame) == "jsq");
        Assert.Equal(2, pipe.Codec.Decodes); // Movement and the Ping barrier, once each.
        await pipe.Finish();
    }

    [Theory]
    [InlineData("placement")]
    [InlineData("wrong-turn")]
    [InlineData("insufficient-mp")]
    public async Task Combat_movement_preserves_placement_ownership_and_mp_guards(string refusal)
    {
        await using var pipe = await Pipe.OpenAsync();
        using var fight = new Battle(pipe.Session, start: refusal != "placement");
        if (refusal == "wrong-turn") fight.Fight.NextTurn();
        if (refusal == "insufficient-mp") fight.Player.CurrentMP = 1;
        int before = fight.Player.CurrentMP;
        pipe.Start(Move(fight.Fight.MapId, fight.Path.Select(cell => cell | (1 << 12)), false));
        Assert.Empty(await pipe.DrainToBarrier());
        Assert.Equal(fight.Path[0], fight.Player.CellId);
        Assert.Equal(before, fight.Player.CurrentMP);
        await pipe.Finish();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Typed_walk_waits_for_the_fight_turn_and_rechecks_that_the_fight_still_exists(bool endedWhileQueued)
    {
        await using var pipe = await Pipe.OpenAsync();
        using var fight = new Battle(pipe.Session);
        await fight.Fight.Turno.WaitAsync();
        try
        {
            pipe.Start(Move(fight.Fight.MapId, fight.Path, false));
            Assert.Equal(0, pipe.Session.UnoCadaVez.CurrentCount);
            Assert.Equal(fight.Path[0], fight.Player.CellId);
            Assert.Equal(6, fight.Player.CurrentMP);
            if (endedWhileQueued)
            {
                Active.TryRemove(fight.Fight.FightId, out _);
                pipe.Session.State.IsInFight = false;
                pipe.Session.State.FightId = 0;
            }
        }
        finally { fight.Fight.Turno.Release(); }
        var frames = await pipe.DrainToBarrier();
        Assert.Equal(endedWhileQueued ? fight.Path[0] : fight.Path[^1], fight.Player.CellId);
        Assert.Equal(endedWhileQueued ? 6 : 4, fight.Player.CurrentMP);
        if (endedWhileQueued) Assert.Empty(frames);
        await pipe.Finish();
    }

    [Fact]
    public async Task Typed_walk_moves_the_current_controlled_summon_instead_of_its_owner()
    {
        await using var pipe = await Pipe.OpenAsync();
        using var fight = new Battle(pipe.Session);
        var floor = MapManager.GetFightWalkable(fight.Fight.MapId)!;
        fight.Player.CellId = floor.First(cell => !fight.Path.Contains(cell) && cell != fight.Fight.Rojo[0].CellId);
        int ownerCell = fight.Player.CellId;
        var summon = new Fighter { Id = -91_008, Invocador = pipe.Session.CharacterId, IsMonster = true,
            CellId = fight.Path[0], MaxHP = 100, CurrentHP = 100, MaxMP = 6, CurrentMP = 6 };
        fight.Fight.Azul.Add(summon);
        fight.Fight.TurnOrder.Insert(0, summon);
        pipe.Start(Move(fight.Fight.MapId, fight.Path, false));
        var frames = await pipe.DrainToBarrier();
        Assert.Equal(ownerCell, fight.Player.CellId);
        Assert.Equal(fight.Path[^1], summon.CellId);
        Assert.Equal(4, summon.CurrentMP);
        byte[] moved = Assert.Single(frames, frame => ConnectionProtocol.ReadOpcode(frame) == "jsj");
        Assert.Equal(summon.Id, Assert.IsType<GameMapMovementEvent>(Codec.Decode(moved).Content).CharacterId);
        await pipe.Finish();
    }

    [Fact]
    public async Task Typed_walk_flushes_a_pending_fight_end_instead_of_spending_more_mp()
    {
        await using var pipe = await Pipe.OpenAsync();
        using var fight = new Battle(pipe.Session);
        pipe.Session.State.RoleplayMapId = fight.Fight.RoleplayMapId;
        pipe.Session.State.RoleplayCellId = fight.Path[0];
        fight.Fight.Rojo[0].CurrentHP = 0;
        fight.Fight.FinPendiente = FightInstance.WaitsForTheJwz;
        pipe.Start(Move(fight.Fight.MapId, fight.Path, false));
        var frames = await pipe.DrainToBarrier();
        Assert.Equal(0, fight.Fight.FinPendiente);
        Assert.False(pipe.Session.State.IsInFight);
        Assert.Equal(fight.Path[0], fight.Player.CellId);
        Assert.Equal(6, fight.Player.CurrentMP);
        Assert.Single(frames, frame => ConnectionProtocol.ReadOpcode(frame) == "jyg");
        Assert.DoesNotContain(frames, frame => ConnectionProtocol.ReadOpcode(frame) == "jsj");
        await pipe.Finish();
    }

    [Fact]
    public async Task Combat_ignores_roleplay_confirmation_cancellation_and_map_change_without_flushing_a_pending_end()
    {
        await using var pipe = await Pipe.OpenAsync();
        using var fight = new Battle(pipe.Session);
        var state = pipe.Session.State;
        state.PendingMovementMapId = state.MapId;
        state.PendingMovementCellId = state.CellId;
        fight.Fight.FinPendiente = FightInstance.WaitsForTheJwz;
        PendingJoins[state.CharacterId] = (fight.Fight, FightInstance.Azules);
        pipe.Start(Codec.Encode(new(new MapMovementConfirmRequest(), 0)));
        await pipe.Send(Codec.Encode(new(new MapMovementCancelRequest(fight.Path[^1]), -1)));
        await pipe.Send(Codec.Encode(new(new MapChangeRequest(154010884, false), 2)));
        Assert.Empty(await pipe.DrainToBarrier());
        Assert.Equal(fight.Path[0], state.CellId);
        Assert.Equal(fight.Fight.MapId, state.MapId);
        Assert.Equal(state.MapId, state.PendingMovementMapId);
        Assert.Equal(state.CellId, state.PendingMovementCellId);
        Assert.Equal(FightInstance.WaitsForTheJwz, fight.Fight.FinPendiente);
        Assert.True(PendingJoins.ContainsKey(state.CharacterId));
        await pipe.Finish();
    }

    [Fact]
    public async Task Malformed_and_event_shaped_movement_never_fall_back_to_a_raw_combat_walk()
    {
        await using var pipe = await Pipe.OpenAsync();
        using var fight = new Battle(pipe.Session);
        pipe.Start(Request("jrw", [0x12, 0x01, 0x80]));
        await pipe.Send(ConnectionProtocol.Push("jrw", Pb.New().Var(1, fight.Fight.MapId)
            .Packed(2, fight.Path.Select(cell => (long)(cell | (1 << 12)))).Build()));
        Assert.Empty(await pipe.DrainToBarrier());
        Assert.Equal(fight.Path[0], fight.Player.CellId);
        Assert.Equal(6, fight.Player.CurrentMP);
        await pipe.Finish();
    }

    [Fact]
    public async Task Roleplay_confirmation_autojoins_pending_fight_and_gets_exactly_one_reply_before_entry()
    {
        await using var pipe = await Pipe.OpenAsync();
        using var fight = new Battle(pipe.Session, start: false, includeOwner: false);
        var state = pipe.Session.State;
        state.IsInFight = false;
        state.FightId = 0;
        state.PendingMovementMapId = state.MapId;
        state.PendingMovementCellId = state.CellId;
        PendingJoins[state.CharacterId] = (fight.Fight, FightInstance.Azules);
        pipe.Start(Codec.Encode(new(new MapMovementConfirmRequest(), -19)));
        var frames = await pipe.DrainToBarrier();

        Assert.True(state.IsInFight);
        Assert.Equal(fight.Fight.FightId, state.FightId);
        var reply = Codec.Decode(Assert.Single(frames, frame => ConnectionProtocol.ReadOpcode(frame) == "jsq"));
        Assert.IsType<MapMovementConfirmedResponse>(reply.Content);
        Assert.Equal(-19, reply.Uid);
        Assert.Equal("jsq", ConnectionProtocol.ReadOpcode(frames[0]));
        Assert.Contains(frames, frame => ConnectionProtocol.ReadOpcode(frame) == "kml");
        Assert.False(PendingJoins.ContainsKey(state.CharacterId));
        Assert.Equal(0, state.PendingMovementMapId);
        Assert.Equal(-1, state.PendingMovementCellId);
        await pipe.Finish();
    }

    [Fact]
    public async Task Typed_border_crossing_preserves_departure_load_clock_and_zaap_order()
    {
        await using var pipe = await Pipe.OpenAsync();
        const int from = 154010883, target = 154010884;
        if (MapManager.GetMapInfo(from) == null) MapManager.Initialize();
        Assert.NotNull(MapManager.GetMapInfo(target));
        pipe.Session.State.MapId = from;
        pipe.Session.State.CellId = 9;
        var earliest = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        pipe.Start(Codec.Encode(new(new MapChangeRequest(target, false), -71)));
        var frames = await pipe.DrainToBarrier();
        Assert.Equal(target, pipe.Session.MapId);
        Assert.Collection(AssertTypedEvents(pipe, frames),
            frame => Assert.Equal(new MapChangeOrientationEvent(pipe.Session.CharacterId, Direction.North), frame.Content),
            frame => Assert.Equal(new MapCurrentEvent(target), frame.Content),
            frame =>
            {
                var time = Assert.IsType<TimeEvent>(frame.Content);
                Assert.Equal(120, time.TimezoneOffset);
                Assert.InRange(time.Timestamp, earliest, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            },
            frame => Assert.Equal(EntryEnvironmentMessages.Live.Zaaps(target).Destinations,
                Assert.IsType<ZaapKnownListEvent>(frame.Content).Destinations));
        await pipe.Finish();
    }

    [Fact]
    public async Task Typed_border_crossing_sends_only_a_notice_when_jailed()
    {
        await using var pipe = await Pipe.OpenAsync();
        const int from = 154010883, target = 154010884, cell = 9;
        pipe.Session.State.MapId = from;
        pipe.Session.State.CellId = cell;
        long id = pipe.Session.CharacterId;
        Jail.PutForTests(new Jail.Prisoner(id, "Walker", DateTime.UtcNow.AddSeconds(290), from, cell, 0));
        try
        {
            var expected = Jail.HoldNotice(pipe.Session);
            Assert.False(string.IsNullOrEmpty(expected));
            pipe.Start(Codec.Encode(new(new MapChangeRequest(target, false), -71)));
            var frames = await pipe.DrainToBarrier();
            var notice = Assert.IsType<TextInformationEvent>(Assert.Single(AssertTypedEvents(pipe, frames)).Content);
            Assert.Equal(TextInformationType.TextInformationMessage, notice.MessageType);
            Assert.Equal(InfoMessages.FreeText, notice.MessageId);
            Assert.Equal(expected, Assert.Single(notice.Parameters));
            Assert.Equal((from, cell), (pipe.Session.MapId, pipe.Session.State.CellId));
            await pipe.Finish();
        }
        finally { Jail.ForgetForTests(id); }
    }

    private static GameFrame[] AssertTypedEvents(Pipe pipe, IReadOnlyList<byte[]> packets)
    {
        // The wire alone cannot distinguish a mapped send from the old raw builders.
        var encoded = pipe.Codec.Encoded.TakeWhile(frame => frame.Content is not PongEvent).ToArray();
        Assert.Equal(packets.Count, encoded.Length);
        for (int i = 0; i < encoded.Length; i++)
        {
            Assert.Null(encoded[i].Uid);
            Assert.IsAssignableFrom<GameEvent>(encoded[i].Content);
            Assert.Equal(Codec.Encode(encoded[i]), packets[i]);
        }
        return packets.Select(packet => Codec.Decode(packet)).ToArray();
    }

    private sealed class Battle : IDisposable
    {
        private static long nextFight = 9_100_880;
        private readonly long owner;
        internal FightInstance Fight { get; }
        internal Fighter Player { get; }
        internal int[] Path { get; }

        internal Battle(GameSession session, bool start = true, bool includeOwner = true)
        {
            owner = session.CharacterId;
            long map = CharacterCreationHandler.StartingMap;
            if (MapManager.GetMapInfo(map) == null) MapManager.Initialize();
            var floor = MapManager.GetFightWalkable(map)!;
            Assert.NotEmpty(floor);
            Path = floor.OrderBy(cell => cell).Select(cell =>
            {
                var (x, y) = MapGeometry.CellToPoint(cell);
                return new[] { cell, MapGeometry.PointToCell(x + 1, y), MapGeometry.PointToCell(x + 2, y) };
            }).First(path => path.All(floor.Contains));
            int far = floor.OrderByDescending(cell => MapGeometry.Distance(Path[0], cell)).First();
            Fight = new FightInstance(Interlocked.Increment(ref nextFight), map, map) { Reglas = FightRules.Desafio };
            Fight.SetPlacementCells(floor.OrderBy(cell => cell).Take(8), floor.OrderByDescending(cell => cell).Take(8));
            Player = new Fighter { Id = includeOwner ? owner : owner + 1, Name = "Walker", Level = 200,
                MaxHP = 1000, CurrentHP = 1000, MaxAP = 7, CurrentAP = 7, MaxMP = 6, CurrentMP = 6,
                Initiative = 9000, CellId = Path[0] };
            Fight.AddPlayer(Player);
            Fight.AddOpponent(new Fighter { Id = owner + 2, Name = "Opponent", Level = 200,
                MaxHP = 1000, CurrentHP = 1000, MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3,
                Initiative = 1, CellId = far });
            if (start) Fight.StartFight();
            // Placement allocation may move fighters; use a known straight, unobstructed path.
            Player.CellId = Path[0];
            Fight.Rojo[0].CellId = far;
            Player.CurrentMP = 6;
            session.State.MapId = map;
            session.State.CellId = Path[0];
            session.State.CharacterLevel = 200;
            session.State.Breed = 8;
            session.State.IsInFight = includeOwner;
            session.State.FightId = includeOwner ? Fight.FightId : 0;
            Assert.True(Active.TryAdd(Fight.FightId, Fight));
        }

        public void Dispose()
        {
            PendingJoins.TryRemove(owner, out _);
            Active.TryRemove(Fight.FightId, out _);
            Fight.CancelPlacementTimer();
            Fight.CancelTurnTimer();
        }
    }

    private sealed class CountingCodec : IGameCodec
    {
        internal int Decodes;
        internal readonly ConcurrentQueue<GameFrame> Encoded = new();
        public GameFrame Decode(ReadOnlySpan<byte> payload) { Decodes++; return Codec.Decode(payload); }
        public byte[] Encode(GameFrame frame) { Encoded.Enqueue(frame); return Codec.Encode(frame); }
    }

    private sealed class Pipe(TcpListener listener, TcpClient client, TcpClient server) : IAsyncDisposable
    {
        private static long nextCharacter = 9_100_880_000;
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
        private Task loop = Task.CompletedTask;
        private readonly NetworkStream clientStream = client.GetStream();
        internal CountingCodec Codec { get; } = new();
        internal GameSession Session { get; private set; } = null!;

        internal static async Task<Pipe> OpenAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port).WaitAsync(Timeout);
                var server = await listener.AcceptTcpClientAsync().WaitAsync(Timeout);
                var pipe = new Pipe(listener, client, server);
                pipe.Session = new(server.GetStream(), pipe.Codec);
                long id = Interlocked.Add(ref nextCharacter, 10);
                pipe.Session.BindAccount(id, 1);
                pipe.Session.State.CharacterId = id;
                pipe.Session.EnterWorld();
                Assert.True(SessionRegistry.Register(pipe.Session));
                return pipe;
            }
            catch { client.Dispose(); listener.Stop(); throw; }
        }

        internal void Start(byte[] first)
        {
            GameNodeProxy.Start(0);
            loop = GameNodeProxy.HandleGameNodeSessionAsync(Session, server.GetStream(), first, "");
        }

        internal Task Send(byte[] frame) => Jondo.Protocol.NetworkMessage.WriteFrameAsync(clientStream, frame).WaitAsync(Timeout);

        internal async Task<List<byte[]>> DrainToBarrier()
        {
            await Send(TypedCombatMovementTests.Codec.Encode(new(new PingRequest(false), 999)));
            byte[] pong = TypedCombatMovementTests.Codec.Encode(new(new PongEvent(false)));
            var frames = new List<byte[]>();
            for (int i = 0; i < 100; i++)
            {
                byte[] frame = await Jondo.Protocol.NetworkMessage.ReadFrameAsync(clientStream).WaitAsync(Timeout);
                if (frame.AsSpan().SequenceEqual(pong)) return frames;
                frames.Add(frame);
            }
            throw new InvalidOperationException("Movement did not reach the Ping barrier.");
        }

        internal async Task Finish()
        {
            client.Client.Shutdown(SocketShutdown.Send);
            await loop.WaitAsync(Timeout);
            Assert.False(clientStream.DataAvailable);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            try { await loop.WaitAsync(Timeout); }
            finally
            {
                SessionRegistry.Unregister(Session);
                GameNodeProxy.Stop();
                server.Dispose();
                listener.Stop();
            }
        }
    }
}
