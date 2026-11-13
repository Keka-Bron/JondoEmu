using System.Text.Json;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class EntryStatusTests
{
    private static readonly GameCodec Wire = new();

    [Fact]
    public void Environment_snapshots_detach_ordered_destinations_reload_sources_and_report_missing_calendar()
    {
        var destinations = new List<long> { 9_000_000_001, 71_001, 9_000_000_001 };
        int? today = 81_001;
        var logs = new List<string>();
        var service = new EntryEnvironmentMessages(() => destinations, () => today, logs.Add);
        var before = service.Zaaps();
        destinations.Clear();
        Assert.Equal(new long[] { 9_000_000_001, 71_001, 9_000_000_001 }, before.Destinations);
        Assert.Empty(service.Zaaps().Destinations);
        Assert.Equal(new AlmanachCalendarDateEvent(81_001), service.Calendar());
        today = 81_002;
        Assert.Equal(new AlmanachCalendarDateEvent(81_002), service.Calendar());
        today = 0;
        Assert.Equal(new AlmanachCalendarDateEvent(0), service.Calendar());
        today = null;
        Assert.Null(service.Calendar());
        Assert.Single(logs);
    }

    [Fact]
    public void Environment_source_failures_propagate_without_being_replaced_with_defaults()
    {
        var failure = new IOException("catalogue failed");
        var service = new EntryEnvironmentMessages(() => throw failure, () => throw failure, _ => throw new Exception("Unexpected log"));
        Assert.Same(failure, Assert.Throws<IOException>(service.Zaaps));
        Assert.Same(failure, Assert.Throws<IOException>(service.Calendar));
    }

    [Fact]
    public async Task Live_discovery_uses_the_explicit_owner_and_keeps_int64_map_ids()
    {
        var own = new StatusRecipient();
        var other = new StatusRecipient();
        var service = new EntryEnvironmentMessages(() => new long[] { 71_001 }, () => null, _ => { },
                                                   map => map == 9_000_000_001);
        using var context = SessionContext.Push(other.Session);
        using var turn = await own.Session.EnterTurnAsync();
        await service.SendKnownAsync(own.Session, 9_000_000_001).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new long[] { 71_001, 9_000_000_001 }, Assert.IsType<ZaapKnownListEvent>(Assert.Single(own.Codec.Frames).Content).Destinations);
        Assert.Empty(other.Packets);
        Assert.Equal(0, own.Codec.Decodes);
        Assert.Same(other.Session, SessionContext.Actual);
    }

    /// <summary>
    /// The client replaces its known zaaps with each hjk: arriving anywhere sends them all, and the
    /// arrival map only when it is a zaap. A list of the arrival map alone turned every zaap of the
    /// world map into a «?».
    /// </summary>
    [Fact]
    public void Arriving_on_a_map_sends_every_known_zaap_and_the_map_only_when_it_is_one()
    {
        var service = new EntryEnvironmentMessages(() => new long[] { 71_001, 72_002 }, () => null, _ => { },
                                                   map => map is 71_001 or 73_003);

        Assert.Equal(new long[] { 71_001, 72_002 }, service.Zaaps(99_999).Destinations);   // not a zaap
        Assert.Equal(new long[] { 71_001, 72_002 }, service.Zaaps(71_001).Destinations);   // known already
        Assert.Equal(new long[] { 71_001, 72_002, 73_003 }, service.Zaaps(73_003).Destinations);
    }

    [Fact]
    public async Task Subscriptions_are_detached_session_owned_full_snapshots_with_disabled_winning_and_opaque_known_slots_retained()
    {
        var own = new StatusRecipient();
        var other = new StatusRecipient();
        ChatChannel[] active = [ChatChannel.Private, ChatChannel.General, ChatChannel.Private, ChatChannel.Unknown17];
        using var context = SessionContext.Push(other.Session);
        using var turn = await own.Session.EnterTurnAsync();
        await ChatChannelMessages.SubscribeAsync(own.Session, new(active, [ChatChannel.General])).WaitAsync(TimeSpan.FromSeconds(5));
        active[0] = ChatChannel.Trade;
        var snapshot = Assert.IsType<ChatChannelsEnabledEvent>(own.Codec.Frames[0].Content);
        Assert.Equal(new[] { ChatChannel.Private, ChatChannel.Unknown17 }, snapshot.ActiveChannels);
        Assert.Contains(ChatChannel.General, snapshot.DisabledChannels);
        Assert.DoesNotContain(ChatChannel.Trade, ChatChannelMessages.Snapshot(own.Session.State).ActiveChannels);
        var updates = own.Codec.Frames.Skip(1).Select(frame => Assert.IsType<ChannelUpdateEvent>(frame.Content)).ToArray();
        Assert.Equal(20, updates.Length);
        Assert.Equal(snapshot.ActiveChannels, updates.Where(update => update.Enable).Select(update => update.Channel));
        Assert.Equal(20, updates.Select(update => update.Channel).Distinct().Count());
        Assert.All(own.Codec.Frames, frame => Assert.Null(frame.Uid));
        Assert.Empty(other.Packets);
        Assert.Contains(ChatChannel.General, ChatChannelMessages.Snapshot(other.Session.State).ActiveChannels);
        await ChatChannelMessages.SubscribeAsync(own.Session, new([], []));
        Assert.Empty(ChatChannelMessages.Snapshot(own.Session.State).ActiveChannels);
        Assert.Same(other.Session, SessionContext.Actual);
    }

    [Fact]
    public async Task Unsupported_subscription_values_fail_before_state_or_delivery_changes()
    {
        var own = new StatusRecipient();
        var before = JsonSerializer.Serialize(ChatChannelMessages.Snapshot(own.Session.State));
        await Assert.ThrowsAsync<GameConversionException>(() => ChatChannelMessages.SubscribeAsync(own.Session,
            new([ChatChannel.General], [(ChatChannel)999])));
        Assert.Equal(before, JsonSerializer.Serialize(ChatChannelMessages.Snapshot(own.Session.State)));
        Assert.Empty(own.Packets);
    }

    [Fact]
    public async Task Subscription_snapshot_and_updates_capture_one_selection_before_awaiting_transport()
    {
        StatusRecipient? own = null;
        own = new StatusRecipient(_ =>
        {
            own!.Session.State.ActiveChatChannels = [ChatChannel.General];
            return Task.CompletedTask;
        });
        await ChatChannelMessages.SubscribeAsync(own.Session, new([ChatChannel.Private], []));
        Assert.Equal(new[] { ChatChannel.Private }, Assert.IsType<ChatChannelsEnabledEvent>(own.Codec.Frames[0].Content).ActiveChannels);
        var enabled = own.Codec.Frames.Skip(1).Select(frame => Assert.IsType<ChannelUpdateEvent>(frame.Content)).Where(update => update.Enable);
        Assert.Equal(new[] { ChatChannel.Private }, enabled.Select(update => update.Channel));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Subscription_delivery_failure_retains_the_selected_state_and_stops_without_retry(bool codecFailure)
    {
        var failure = new IOException("channel delivery failed");
        var own = new StatusRecipient(codecFailure ? null : _ => Task.FromException(failure));
        if (codecFailure) own.Codec.Failure = failure;
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => ChatChannelMessages.SubscribeAsync(own.Session,
            new([ChatChannel.Private], []))));
        Assert.Equal(new[] { ChatChannel.Private }, ChatChannelMessages.Snapshot(own.Session.State).ActiveChannels);
        Assert.Single(own.Codec.Frames);
        Assert.Equal(codecFailure ? 0 : 1, own.Packets.Count);
    }

}
