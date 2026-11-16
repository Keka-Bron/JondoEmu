using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Xunit;

namespace Jondo.Unity.Tests.World;

[Collection("conquests")]
public sealed class PrismListFlowTests : IDisposable
{
    private const long Owner = 9_968_000_001, Account = 8_968_000_001, Map = 9_968_000_001;
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"jondo-prism-lists-{Guid.NewGuid():N}.db");

    public PrismListFlowTests()
    {
        AllianceStore.ConnectionStringOverride = $"Data Source={_file}";
        PrismStore.ConnectionStringOverride = $"Data Source={_file}";
        MapManager.Initialize();
        MapManager.Maps[Map] = new() { MapId = Map, PosX = -37, PosY = 59, SubAreaId = 81_001 };
    }

    public void Dispose()
    {
        MapManager.Maps.Remove(Map);
        PrismStore.ConnectionStringOverride = null;
        AllianceStore.ConnectionStringOverride = null;
        TestDatabases.ReleasePool(_file);
        File.Delete(_file);
    }

    [Fact]
    public async Task Entry_uses_explicit_owner_order_filters_and_detached_own_and_foreign_views()
    {
        var mine = AllianceStore.Create(Owner, "OwnAlliance", "OWN", 1, 2, 3, 4, 5);
        var foreign = AllianceStore.Create(Owner + 1, "ForeignAlliance", "FOR", 6, 7, 8, 9, 10);
        var ownPrism = PrismStore.Place(mine.Id, 81_002, Map, 301, 1);
        ownPrism.CrystalsUsed = 4;
        ownPrism.State = PrismStore.Weakened;
        ownPrism.NextChangeUtcMs = 8_000_000_007;
        var other = PrismStore.Place(foreign.Id, 81_001, Map + 1, 302, 2);
        PrismStore.Place(long.MaxValue, 81_003, Map + 2, 303, 3); // dangling alliance is skipped
        var codec = new RecordingCodec();
        await using var pipe = await ClientPipe.OpenAsync(Account, Owner, Map, codec: codec);
        var wrong = GameSession.SinSocket();
        wrong.State.CharacterId = Owner + 1;
        using var context = SessionContext.Push(wrong);
        using var turn = await pipe.Session.EnterTurnAsync();
        await PrismHandler.OnWorldEntryAsync(pipe.Session).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 0x72, 1 }, await Packet(pipe));
        var snapshot = Assert.IsType<PrismListEvent>(Assert.Single(codec.Frames).Content);
        Assert.Equal(new[] { 81_001, 81_002 }, snapshot.Prisms.Select(p => p.SubAreaId));
        var own = snapshot.Prisms[1];
        Assert.Equal(Map, own.MapId);
        Assert.Equal(new MapCoordinates(-37, 59), own.MapCoordinates);
        Assert.Equal(new MapCoordinates(0, 0), snapshot.Prisms[0].MapCoordinates);
        Assert.Equal(0, own.Prism!.CristalNumberLeft);
        Assert.NotNull(own.Prism.Cristal!.CristalObject.Item);
        Assert.Equal(0, own.Prism.Cristal.CristalObject.Item.Gid);
        Assert.Equal(8_000_000_007, own.Prism.NextEvolutionDate);
        Assert.Equal(mine.Uuid, own.Prism.AllianceInformation!.Uid);
        Assert.Null(snapshot.Prisms[0].Prism!.CristalNumberLeft);
        Assert.Null(snapshot.Prisms[0].Prism!.Cristal);
        ownPrism.CrystalsUsed = 0;
        ownPrism.NextChangeUtcMs = 1;
        PrismStore.Remove(other);
        Assert.Equal(2, snapshot.Prisms.Count);
        Assert.Equal(0, own.Prism.CristalNumberLeft);
        Assert.Equal(8_000_000_007, own.Prism.NextEvolutionDate);
        Assert.Equal(0, pipe.Session.UnoCadaVez.CurrentCount);
        Assert.Same(wrong, SessionContext.Actual);
        Assert.Null(codec.Frames[0].Uid);
        Assert.Equal(0, codec.Decodes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Real_framed_entry_sends_empty_replacements_or_large_map_own_crystal_views(int scenario)
    {
        if (scenario > 0)
        {
            var alliance = AllianceStore.Create(Owner, "FramedAlliance", "FRA", 1, 2, 3, 4, 5);
            var prism = PrismStore.Place(alliance.Id, 81_001, Map, 301, 1);
            prism.CrystalsUsed = 4;
            if (scenario == 2)
            {
                prism.CrystalGid = 27632;
                prism.State = PrismStore.Pacified;
            }
        }
        await using var pipe = await ClientPipe.OpenAsync(Account, Owner, Map);
        await PrismHandler.OnWorldEntryAsync(pipe.Session);
        var codec = new GameCodec();
        var frame = codec.Decode(await Packet(pipe));
        Assert.Null(frame.Uid);
        var list = Assert.IsType<PrismListEvent>(frame.Content);
        if (scenario == 0) Assert.Empty(list.Prisms);
        else
        {
            var prism = Assert.Single(list.Prisms);
            Assert.Equal(Map, prism.MapId);
            Assert.Equal(new MapCoordinates(-37, 59), prism.MapCoordinates);
            Assert.Equal(0, prism.Prism!.CristalNumberLeft);
            var crystal = prism.Prism.Cristal!;
            Assert.Equal(scenario == 2 ? 27632 : 0, crystal.CristalObject.Item.Gid);
            Assert.Equal(scenario == 2 ? PrismCristalType.Protective : PrismCristalType.NoCristal, crystal.Type);
            if (scenario == 2)
            {
                Assert.Equal(1, crystal.CristalObject.Item.Quantity);
                Assert.Equal(PrismProtocol.CrystalUid(Assert.Single(PrismStore.All())), crystal.CristalObject.Item.Uid);
                Assert.Contains(crystal.CristalObject.Item.Effects, effect => effect.Action == 2996);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Entry_codec_and_transport_failures_propagate_in_the_callers_turn(bool codecFailure)
    {
        var failure = new IOException("prism delivery failed");
        var codec = new RecordingCodec { Failure = codecFailure ? failure : null };
        var session = new GameSession(codec, _ => throw failure);
        session.State.CharacterId = Owner;
        using var turn = await session.EnterTurnAsync();
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => PrismHandler.OnWorldEntryAsync(session)));
        Assert.Single(codec.Frames);
        Assert.Equal(0, session.UnoCadaVez.CurrentCount);
    }

    [Fact]
    public async Task Alliance_source_failure_stops_before_encoding()
    {
        AllianceStore.ConnectionStringOverride = "Data Source=/does-not-exist/jondo-prism-list.db";
        var codec = new RecordingCodec();
        var session = new GameSession(codec, _ => Task.CompletedTask);
        session.State.CharacterId = Owner;
        await Assert.ThrowsAnyAsync<Exception>(() => PrismHandler.OnWorldEntryAsync(session));
        Assert.Empty(codec.Frames);
    }

    private static async Task<byte[]> Packet(ClientPipe pipe)
        => (await Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer).WaitAsync(TimeSpan.FromSeconds(5)))!;
    private sealed class RecordingCodec : IGameCodec
    {
        internal readonly List<GameFrame> Frames = [];
        internal IOException? Failure;
        internal int Decodes;
        public byte[] Encode(GameFrame frame) { Frames.Add(frame); if (Failure != null) throw Failure; return [0x72, checked((byte)Frames.Count)]; }
        public GameFrame Decode(ReadOnlySpan<byte> packet) { Decodes++; throw new InvalidOperationException("Prism lists must not reparse packets."); }
    }
}
