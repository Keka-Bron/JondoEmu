using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class GiftMessagesTests
{
    [Fact]
    public void Snapshot_keeps_source_order_redemption_ids_and_item_previews_and_is_detached()
    {
        var source = new List<Gifts.Gift>
        {
            new() { Id = 91_002, Gid = 81_001, Quantity = 3, Title = "Cadeau ☀", Description = "Stored only" },
            new() { Id = 91_001, Gid = 81_002, Quantity = 1, Title = "" },
        };
        var snapshot = GiftMessages.Snapshot(source);
        source.Clear();
        Assert.Equal(new long[] { 91_002, 91_001 }, snapshot.Actions.Select(action => Assert.Single(action.ItemUids)));
        Assert.Equal(new[] { "Cadeau ☀", "" }, snapshot.Actions.Select(action => action.Content!.Title));
        var first = Assert.Single(snapshot.Actions[0].Content!.Items);
        Assert.Equal((3, 81_001), (first.Quantity, first.ObjectGid));
        Assert.Empty(first.Effects);
        var second = Assert.Single(snapshot.Actions[1].Content!.Items);
        Assert.Equal((1, 81_002), (second.Quantity, second.ObjectGid));
        Assert.Empty(second.Effects);
        Assert.Empty(GiftMessages.Snapshot([]).Actions);
    }

    [Fact]
    public async Task Direct_send_grants_due_rewards_before_reading_the_owning_accounts_gifts()
    {
        var own = new GiftTestRecipient(71_001);
        var wrong = new GiftTestRecipient(71_002);
        var calls = new List<string>();
        var gifts = new List<Gifts.Gift>();
        var service = new GiftService(account =>
        {
            Assert.Equal(71_001, account);
            calls.Add("grant");
            gifts.Add(new() { Id = 91_001, Gid = 81_001, Quantity = 5, Title = "Earned reward" });
        }, account =>
        {
            Assert.Equal(71_001, account);
            calls.Add("read");
            return gifts;
        }, () => throw new Exception("No broadcast expected"), _ => throw new Exception("No log expected"));
        using var context = SessionContext.Push(wrong.Session);
        using var turn = await own.Session.EnterTurnAsync();
        await service.SendAsync(own.Session).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "grant", "read" }, calls);
        var frame = Assert.Single(own.Codec.Frames);
        Assert.Null(frame.Uid);
        var action = Assert.Single(Assert.IsType<GameActionItemListEvent>(frame.Content).Actions);
        Assert.Equal(91_001, Assert.Single(action.ItemUids));
        Assert.Equal(5, Assert.Single(action.Content!.Items).Quantity);
        Assert.Empty(wrong.Codec.Frames);
        Assert.Empty(wrong.Packets);
        Assert.Equal(0, own.Codec.Decodes);
        Assert.Same(wrong.Session, SessionContext.Actual);
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("read")]
    public async Task Source_failure_propagates_before_any_delivery(string stage)
    {
        var failure = new IOException("gift source failed");
        var calls = new List<string>();
        var service = new GiftService(_ =>
        {
            calls.Add("grant");
            if (stage == "grant") throw failure;
        }, _ => { calls.Add("read"); throw failure; }, () => [], _ => { });
        var own = new GiftTestRecipient(71_001);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => service.SendAsync(own.Session)));
        Assert.Equal(stage == "grant" ? new[] { "grant" } : new[] { "grant", "read" }, calls);
        Assert.Empty(own.Packets);
        Assert.Empty(own.Codec.Frames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Live_refresh_snapshots_once_uses_each_recipients_codec_and_continues_after_failure(bool codecFailure)
    {
        var failure = new IOException("recipient disconnected");
        var source = new List<Gifts.Gift> { new() { Id = 91_001, Gid = 81_001, Quantity = 2, Title = "Gift" } };
        var failed = new GiftTestRecipient(71_001, codecFailure ? _ => failure : null,
            _ => { source.Clear(); return Task.FromException(failure); });
        var otherAccount = new GiftTestRecipient(71_002);
        var later = new GiftTestRecipient(71_001);
        int grants = 0, reads = 0;
        var logs = new List<string>();
        var service = new GiftService(account => { Assert.Equal(71_001, account); grants++; },
            account => { Assert.Equal(71_001, account); reads++; return source; },
            () => [failed.Session, otherAccount.Session, later.Session], logs.Add);
        await service.BroadcastAsync(71_001);

        Assert.Equal(1, grants);
        Assert.Equal(1, reads);
        Assert.Single(logs);
        var firstFrame = Assert.Single(failed.Codec.Frames);
        var lastFrame = Assert.Single(later.Codec.Frames);
        Assert.Same(firstFrame.Content, lastFrame.Content);
        var gift = Assert.Single(Assert.IsType<GameActionItemListEvent>(lastFrame.Content).Actions);
        Assert.Equal(91_001, Assert.Single(gift.ItemUids));
        Assert.Equal(2, Assert.Single(gift.Content!.Items).Quantity);
        Assert.Empty(otherAccount.Codec.Frames);
        Assert.Empty(otherAccount.Packets);
        Assert.Single(later.Packets);
        Assert.Equal(codecFailure ? 0 : 1, failed.Packets.Count);
        Assert.Equal(0, later.Codec.Decodes);
    }

    [Fact]
    public async Task Empty_live_refresh_sends_an_empty_replacement()
    {
        var own = new GiftTestRecipient(71_001);
        await new GiftService(_ => { }, _ => [], () => [own.Session], _ => { }).BroadcastAsync(71_001);
        Assert.Empty(Assert.IsType<GameActionItemListEvent>(Assert.Single(own.Codec.Frames).Content).Actions);
    }
}

internal sealed class GiftTestRecipient
{
    internal List<byte[]> Packets { get; } = [];
    internal GiftTestCodec Codec { get; }
    internal GameSession Session { get; }

    internal GiftTestRecipient(long account, Func<GameFrame, Exception?>? failure = null, Func<byte[], Task>? send = null)
    {
        Codec = new(failure);
        Session = new(Codec, async packet => { Packets.Add(packet); if (send != null) await send(packet); });
        Session.BindAccount(account, 1);
    }
}

internal sealed class GiftTestCodec(Func<GameFrame, Exception?>? failure) : IGameCodec
{
    private static readonly GameCodec Wire = new();
    internal List<GameFrame> Frames { get; } = [];
    internal int Decodes { get; private set; }
    public GameFrame Decode(ReadOnlySpan<byte> packet) { Decodes++; throw new Exception("Unexpected production decode"); }
    public byte[] Encode(GameFrame frame)
    {
        Frames.Add(frame);
        if (failure?.Invoke(frame) is { } error) throw error;
        return Wire.Encode(frame);
    }
}
