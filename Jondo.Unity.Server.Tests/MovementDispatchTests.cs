using Google.Protobuf;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Protocol.Game.Wire.Messages;
using Jondo.Unity.Server.Network;
using Xunit;
using Recipient = Jondo.Unity.Server.Tests.GameMessagingTests.Recipient;

namespace Jondo.Unity.Server.Tests;

public sealed class MovementDispatchTests
{
    private static readonly GameCodec Codec = new();

    private static Task<bool> Dispatch(GameSession session, byte[] bytes) =>
        MovementDispatch.TryHandleAsync(session, GameNodeProxy.DecodeRequest(session, bytes));

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public async Task Confirmation_replies_to_the_requesting_session_before_consuming_pending_movement(int uid)
    {
        const long map = 12_000_020;
        using var unrelated = new Recipient(map);
        var responses = new List<byte[]>();
        GameSession? requester = null;
        requester = new GameSession(Codec, packet =>
        {
            Assert.Equal(map, requester!.State.PendingMovementMapId);
            responses.Add(packet);
            return Task.CompletedTask;
        });
        requester.BindAccount(99_000_001, 1);
        requester.State.CharacterId = 99_000_001;
        requester.State.MapId = map;
        requester.State.CellId = 42;
        requester.State.PendingMovementMapId = map;
        requester.State.PendingMovementCellId = 42;
        requester.EnterWorld();
        using (SessionContext.Push(unrelated.Session))
        {
            Assert.True(await Dispatch(requester, Codec.Encode(new(new MapMovementConfirmRequest(), uid))));
            Assert.Same(unrelated.Session, SessionContext.Actual);
        }
        var response = Codec.Decode(Assert.Single(responses));
        Assert.IsType<MapMovementConfirmedResponse>(response.Content);
        Assert.Equal(uid, response.Uid);
        Assert.Empty(unrelated.Packets);
        Assert.Equal(0, requester.State.PendingMovementMapId);
        Assert.Equal(-1, requester.State.PendingMovementCellId);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("wrong-kind")]
    public async Task Bad_requests_do_not_claim_typed_handling_or_mutate_state(string scenario)
    {
        using var target = new Recipient(12_000_021);
        target.Session.State.CellId = 17;
        byte[] valid = Codec.Encode(new(new MapMovementRequest(target.Session.MapId, [42], false), 12));
#if JONDO_PROTOCOL_3_8_1_2
        var root = hff.Parser.ParseFrom(valid);
        if (scenario == "malformed") root.Focl.Focb.Value = ByteString.CopyFrom([0x12, 0x01, 0x80]);
        else root.Focm = new hfg { Focs = root.Focl.Focb.Clone() };
#else
        var root = hea.Parser.ParseFrom(valid);
        if (scenario == "malformed") root.Fllm.Fllg.Value = ByteString.CopyFrom([0x12, 0x01, 0x80]);
        else root.Flll = new hdx { Fllb = root.Fllm.Fllg.Clone() };
#endif
        Assert.False(await Dispatch(target.Session, root.ToByteArray()));
        Assert.Equal(17, target.Session.State.CellId);
        Assert.Empty(target.Packets);
    }

    [Fact]
    public async Task Combat_consumes_movement_without_entering_roleplay_handlers()
    {
        var codec = new GameMessagingTests.CountingCodec();
        using var target = new Recipient(12_000_022, codec);
        target.Session.State.IsInFight = true;
        target.Session.State.CellId = 17;
        target.Session.State.PendingMovementMapId = target.Session.MapId;
        target.Session.State.PendingMovementCellId = 23;
        int mapChanges = 0;
        foreach (var request in new GameRequest[] { new MapMovementRequest(target.Session.MapId, [1, 2], true),
            new MapMovementCancelRequest(1), new MapMovementConfirmRequest(), new MapChangeRequest(42, false) })
            Assert.True(await MovementDispatch.TryHandleAsync(target.Session,
                GameNodeProxy.DecodeRequest(target.Session, Codec.Encode(new(request, 1))), () => mapChanges++));
        Assert.Equal(17, target.Session.State.CellId);
        Assert.Equal(target.Session.MapId, target.Session.State.PendingMovementMapId);
        Assert.Equal(23, target.Session.State.PendingMovementCellId);
        Assert.Equal(0, mapChanges);
        target.Session.State.IsInFight = false;
        Assert.False(await Dispatch(target.Session, Codec.Encode(new(new PingRequest(false), 2))));
        Assert.Equal(5, codec.Decodes);
        Assert.Empty(target.Packets);
    }

    [Theory]
    [InlineData(0)] // No authentication.
    [InlineData(1)] // No selected character.
    [InlineData(2)] // Not in world.
    public async Task Requests_before_world_entry_are_decoded_but_not_claimed_or_answered(int stage)
    {
        var codec = new GameMessagingTests.CountingCodec();
        var packets = new List<byte[]>();
        var session = new GameSession(codec, bytes => { packets.Add(bytes); return Task.CompletedTask; });
        if (stage > 0) session.BindAccount(99_000_023, 1);
        if (stage > 1) session.State.CharacterId = 99_000_023;
        var entry = new GameEntryService(session, new GameEntryBackend());
        foreach (var request in new GameRequest[] { new MapMovementRequest(42, [1, 2], false),
            new MapMovementConfirmRequest(), new MapMovementCancelRequest(1), new MapChangeRequest(43, false) })
        {
            var frame = GameNodeProxy.DecodeRequest(session, Codec.Encode(new(request, 1)));
            Assert.False(await MovementDispatch.TryHandleAsync(session, frame));
            // The action is refused, but the outer dispatcher still owns its recognized type.
            Assert.Equal(GameEntryResult.Handled, await GameNodeProxy.DispatchTypedAsync(session, frame, entry));
        }
        Assert.Equal(4, codec.Decodes);
        Assert.Empty(packets);
    }

    [Fact]
    public async Task Empty_paths_and_mismatched_maps_do_not_move_or_broadcast()
    {
        using var target = new Recipient(12_000_024);
        target.Session.State.CellId = 17;
        foreach (var request in new[] { new MapMovementRequest(target.Session.MapId, [], false),
            new MapMovementRequest(target.Session.MapId + 1, [1, 2], false) })
            Assert.True(await Dispatch(target.Session, Codec.Encode(new(request, 1))));
        Assert.Equal(17, target.Session.State.CellId);
        Assert.Empty(target.Packets);
    }
}
