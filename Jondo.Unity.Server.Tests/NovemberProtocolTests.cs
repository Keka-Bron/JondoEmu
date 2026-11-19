#if JONDO_PROTOCOL_3_8_1_2
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Server.Network.Connection;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class NovemberProtocolTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unhandled_or_undecodable_November_requests_never_reach_August_routing(bool valid)
    {
        var codec = new GameCodec();
        var session = new GameSession(codec, _ => Task.CompletedTask);
        byte[] request = valid ? codec.Encode(new GameFrame(new ObjectDeleteRequest(new(42, 1)), 7)) : [0xff];
        if (valid) Assert.Equal("iuv", codec.ReadEnvelope(request).Alias); // August's shortcut swap.

        var result = await GameNodeProxy.DispatchPacketAsync(session, request,
            new GameEntryService(session, new GameEntryBackend()),
            () => throw new InvalidOperationException("August dispatcher called for a November packet"));
        Assert.Equal(GameEntryResult.Unhandled, result);
    }

    [Fact]
    public void November_server_selection_can_list_a_breed_21_character()
    {
        var servers = ConnectionAccountProjection.ServerList(
            [new DatabaseManager.DbServer { Id = 290, Type = 1, Joinable = true }],
            [new DbCharacter { ServerId = 290, Name = "Novembre", Breed = 21, Sex = 1, Level = 2 }],
            DateTimeOffset.UnixEpoch);
        var character = Assert.Single(Assert.Single(servers.Servers).Characters);
        Assert.Equal("Novembre", character.Name);
        Assert.Null(character.Breed);
    }

    [Fact]
    public async Task A_November_movement_confirmation_reaches_the_typed_handler()
    {
        byte[] request = Pb.New().Msg(1,
            Pb.New().Var(1, 42).Msg(2, Pb.New().Str(1, "type.ankama.com/jsv"))).Build();
        byte[] expected = Pb.New().Msg(3,
            Pb.New().Msg(1, Pb.New().Str(1, "type.ankama.com/jry")).Var(2, 42)).Build();
        var packets = new List<byte[]>();
        var session = new GameSession(new GameCodec(), packet => { packets.Add(packet); return Task.CompletedTask; });
        session.BindAccount(99_000_001, 1);
        session.State.CharacterId = 99_000_001;
        session.State.MapId = session.State.PendingMovementMapId = 12_000_020;
        session.State.CellId = session.State.PendingMovementCellId = 42;
        session.EnterWorld();

        var frame = GameNodeProxy.DecodeRequest(session, request);
        Assert.IsType<MapMovementConfirmRequest>(frame?.Content);
        Assert.True(await MovementDispatch.TryHandleAsync(session, frame));
        Assert.Equal(expected, Assert.Single(packets));
        Assert.Equal(0, session.State.PendingMovementMapId);
    }
}
#endif
