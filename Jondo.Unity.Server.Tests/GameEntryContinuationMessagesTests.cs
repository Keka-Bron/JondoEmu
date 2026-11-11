using System.Globalization;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class GameEntryContinuationMessagesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Continuation_sends_numeric_connection_identity_then_the_current_accounts_gifts(bool populated)
    {
        var own = new GiftTestRecipient(71_001);
        var wrong = new GiftTestRecipient(71_002);
        int reads = 0;
        var service = new GiftService(_ => { }, account =>
        {
            Assert.Equal(71_001, account);
            Assert.IsType<HaapiSessionEvent>(Assert.Single(own.Codec.Frames).Content);
            reads++;
            return populated ? [new() { Id = 91_001, Gid = 81_001, Quantity = 3, Title = "Current gift" }] : [];
        }, () => [], _ => { });
        using var context = SessionContext.Push(wrong.Session);
        using var turn = await own.Session.EnterTurnAsync();
        int sent = await new GameEntryContinuationMessages(service).SendAsync(own.Session).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, sent);
        Assert.Equal(2, own.Packets.Count);
        var identity = Assert.IsType<HaapiSessionEvent>(own.Codec.Frames[0].Content);
        Assert.Equal(own.Session.HaapiSessionUid, long.Parse(identity.SessionUid, CultureInfo.InvariantCulture));
        Assert.True(own.Session.HaapiSessionUid > 0);
        var gifts = Assert.IsType<GameActionItemListEvent>(own.Codec.Frames[1].Content);
        Assert.Equal(populated ? 1 : 0, gifts.Actions.Count);
        Assert.Equal(1, reads);
        Assert.All(own.Codec.Frames, frame => Assert.Null(frame.Uid));
        Assert.Equal(0, own.Codec.Decodes);
        Assert.Empty(wrong.Packets);
        Assert.Same(wrong.Session, SessionContext.Actual);
    }

    [Fact]
    public async Task Haapi_identity_is_invariant_and_stays_with_the_connection_across_character_changes()
    {
        var own = new GiftTestRecipient(71_001);
        var other = new GiftTestRecipient(71_001);
        var continuation = new GameEntryContinuationMessages(new(_ => { }, _ => [], () => [], _ => { }));
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            own.Session.State.CharacterId = 81_001;
            own.Session.EnterWorld();
            await continuation.SendAsync(own.Session);
            own.Session.LeaveWorld();
            own.Session.State.CharacterId = 0;
            own.Session.State.CharacterId = 81_002;
            own.Session.EnterWorld();
            await continuation.SendAsync(own.Session);
            await continuation.SendAsync(other.Session);
        }
        finally { CultureInfo.CurrentCulture = before; }

        var ids = own.Codec.Frames.Select(frame => frame.Content).OfType<HaapiSessionEvent>().Select(item => item.SessionUid).ToArray();
        Assert.Equal(2, ids.Length);
        Assert.Equal(ids[0], ids[1]);
        Assert.All(ids[0], digit => Assert.InRange(digit, '0', '9'));
        Assert.NotEqual(ids[0], Assert.IsType<HaapiSessionEvent>(other.Codec.Frames[0].Content).SessionUid);
    }

    [Theory]
    [InlineData("haapi", false)]
    [InlineData("haapi", true)]
    [InlineData("gifts", false)]
    [InlineData("gifts", true)]
    public async Task Codec_and_transport_failures_stop_at_the_failed_event_without_retry(string stage, bool transport)
    {
        var failure = new IOException("continuation failed");
        bool IsFailed(GameFrame frame) => stage == "haapi" ? frame.Content is HaapiSessionEvent : frame.Content is GameActionItemListEvent;
        int sends = 0, reads = 0;
        var own = new GiftTestRecipient(71_001, frame => !transport && IsFailed(frame) ? failure : null,
            _ => ++sends == (stage == "haapi" ? 1 : 2) && transport ? Task.FromException(failure) : Task.CompletedTask);
        var service = new GiftService(_ => { }, _ => { reads++; return []; }, () => [], _ => { });
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => new GameEntryContinuationMessages(service).SendAsync(own.Session)));
        Assert.Equal(stage == "haapi" ? 1 : 2, own.Codec.Frames.Count);
        Assert.Equal(stage == "haapi" ? 0 : 1, reads);
        Assert.Single(own.Codec.Frames, IsFailed);
        Assert.Equal((stage == "haapi" ? 1 : 2) - (transport ? 0 : 1), sends);
        Assert.Equal(0, own.Codec.Decodes);
    }

    [Fact]
    public async Task Gift_source_failure_keeps_the_already_sent_identity_without_replaying_it()
    {
        var failure = new IOException("gift read failed");
        var own = new GiftTestRecipient(71_001);
        int reads = 0;
        var service = new GiftService(_ => { }, _ => { reads++; throw failure; }, () => [], _ => { });
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => new GameEntryContinuationMessages(service).SendAsync(own.Session)));
        Assert.IsType<HaapiSessionEvent>(Assert.Single(own.Codec.Frames).Content);
        Assert.Single(own.Packets);
        Assert.Equal(1, reads);
    }
}
