using Google.Protobuf;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Protocol.Game.Wire.Messages;
using Jondo.Unity.Server.Network;
using Xunit;
using Recipient = Jondo.Unity.Server.Tests.GameMessagingTests.Recipient;

namespace Jondo.Unity.Server.Tests;

public sealed class WorldMovementDispatchTests
{
    private static readonly GameCodec Codec = new();

    private static Task<bool> Dispatch(GameSession session, byte[] bytes) =>
        WorldMovementDispatch.TryHandleAsync(session, bytes, Codec.ReadEnvelope(bytes).Content.TypeUrl);

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
    public async Task Bad_migrated_requests_are_consumed_without_mutating_state_or_falling_back(string scenario)
    {
        using var target = new Recipient(12_000_021);
        target.Session.State.CellId = 17;
        var root = hea.Parser.ParseFrom(Codec.Encode(new(new MapMovementRequest(target.Session.MapId, [42], false), 12)));
        string typeUrl = root.Fllm.Fllg.TypeUrl;
        if (scenario == "malformed") root.Fllm.Fllg.Value = ByteString.CopyFrom([0x12, 0x01, 0x80]);
        else root.Flll = new hdx { Fllb = root.Fllm.Fllg.Clone() };
        Assert.True(await WorldMovementDispatch.TryHandleAsync(target.Session, root.ToByteArray(), typeUrl));
        Assert.Equal(17, target.Session.State.CellId);
        Assert.Empty(target.Packets);
    }

    [Fact]
    public async Task Combat_and_unrelated_messages_do_not_enter_typed_world_handlers()
    {
        var codec = new GameMessagingTests.CountingCodec();
        using var target = new Recipient(12_000_022, codec);
        target.Session.State.IsInFight = true;
        foreach (var request in new GameRequest[] { new MapMovementRequest(target.Session.MapId, [1, 2], true),
            new MapMovementCancelRequest(1), new MapMovementConfirmRequest() })
            Assert.False(await Dispatch(target.Session, Codec.Encode(new(request, 1))));
        target.Session.State.IsInFight = false;
        Assert.False(await Dispatch(target.Session, Codec.Encode(new(new PingRequest(false), 2))));
        Assert.Equal(0, codec.Decodes);
        Assert.Empty(target.Packets);
    }

    [Fact]
    public async Task Requests_before_world_entry_are_consumed_without_decoding_or_responding()
    {
        var codec = new GameMessagingTests.CountingCodec();
        using var target = new Recipient(12_000_023, codec, enterWorld: false);
        Assert.True(await Dispatch(target.Session, Codec.Encode(new(new MapMovementConfirmRequest(), 1))));
        Assert.Equal(0, codec.Decodes);
        Assert.Empty(target.Packets);
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
