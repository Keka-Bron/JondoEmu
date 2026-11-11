using System;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Xunit;

namespace Jondo.Unity.Tests.Sessions;

public sealed class EntryChannelFlowTests
{
    [Theory]
    [InlineData(-17)]
    [InlineData(0)]
    public async Task Framed_subscription_dispatch_updates_only_its_owner_and_survives_until_map_initialization(int uid)
    {
        var wire = new GameCodec();
        await using var own = await ClientPipe.OpenAsync(8_198_101, 8_198_001, 8_198_201);
        await using var other = await ClientPipe.OpenAsync(8_198_102, 8_198_002, 8_198_202);
        using var context = SessionContext.Push(other.Session);
        using var turn = await own.Session.EnterTurnAsync();
        var active = new[] { ChatChannel.General, ChatChannel.Private, ChatChannel.Unknown17 };
        var request = new ChatChannelSubscriptionRequest(active, [ChatChannel.General]);
        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(own.FromServer, wire.Encode(new(request, uid)));
        var input = await Jondo.Protocol.NetworkMessage.ReadFrameAsync(own.ToClient).WaitAsync(TimeSpan.FromSeconds(5));
        var decoded = GameEntryDispatch.Decode(own.Session, input, ConnectionProtocol.UriPrefix + "ktn");
        Assert.NotNull(decoded);
        Assert.Equal(uid, decoded.Uid);
        Assert.Equal(GameEntryResult.Handled, await new GameEntryService(own.Session, new GameEntryBackend()).HandleAsync(decoded));
        var snapshot = Assert.IsType<ChatChannelsEnabledEvent>((await Next()).Content);
        Assert.Equal(new[] { ChatChannel.Private, ChatChannel.Unknown17 }, snapshot.ActiveChannels);
        for (int i = 0; i < 20; i++)
        {
            var update = Assert.IsType<ChannelUpdateEvent>((await Next()).Content);
            Assert.Equal((ChatChannel)i, update.Channel);
            Assert.Equal(i is 9 or 17, update.Enable);
        }
        var opening = new MapEntryOpeningMessages(_ => false, _ => 0, _ => [], (_, _) => false, () => []);
        Assert.Equal(34, await new GameEntryMapMessages(TimeProvider.System, () => 5, _ => new([], []),
            _ => (new([], ShortcutBar.Spell), new([], ShortcutBar.General))).SendAsync(own.Session,
            opening, GameContext.RolePlay, own.Session.MapId));
        for (int i = 0; i < 13; i++) await Next();
        for (int i = 0; i < 20; i++)
        {
            var update = Assert.IsType<ChannelUpdateEvent>((await Next()).Content);
            Assert.Equal(snapshot.ActiveChannels.Contains(update.Channel), update.Enable);
        }
        Assert.Equal(new BakApiKeyEvent(""), (await Next()).Content);
        Assert.Null(other.Session.State.ActiveChatChannels);
        Assert.Same(other.Session, SessionContext.Actual);
        await own.Quiet();
        await other.Quiet();

        async Task<GameFrame> Next()
        {
            var packet = await Jondo.Protocol.NetworkMessage.ReadFrameAsync(own.FromServer).WaitAsync(TimeSpan.FromSeconds(5));
            var frame = wire.Decode(packet);
            Assert.Null(frame.Uid);
            return frame;
        }
    }
}
