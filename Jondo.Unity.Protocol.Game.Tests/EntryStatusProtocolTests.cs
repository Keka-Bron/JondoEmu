using Google.Protobuf;
using Jondo.Unity.Protocol.Game.Wire;
using Xunit;

namespace Jondo.Unity.Protocol.Game.Tests;

public sealed class EntryStatusProtocolTests
{
    private readonly GameCodec _codec = new();

    [Theory]
    [InlineData("mcm")]
    [InlineData("mcr")]
    public void Notice_information_distinguishes_absent_from_present_empty_and_keeps_historical_founding_date(string alias)
    {
        Event(alias, [], Notice(alias, null));
        Event(alias, [0x0a, 0], Notice(alias, new("", 0, "", 0)));
        // Joining «E X A M P L E»: only its founding timestamp, in seconds.
        Event(alias, Convert.FromHexString("0a0610b5c1a6ce06"), Notice(alias, new("", 1774821557, "", 0)));
    }

    [Theory]
    [InlineData("mcm", long.MaxValue, long.MinValue)]
    [InlineData("mcr", long.MinValue, long.MaxValue)]
    public void Populated_notices_keep_unicode_and_signed_int64_timestamp_and_author(string alias, long seconds, long author)
        => Event(alias, Msg(1, [.. Text(1, "Éclaireurs ☀\n"), .. Var(2, seconds), .. Text(3, "Auteur"), .. Var(4, author)]),
            Notice(alias, new("Éclaireurs ☀\n", seconds, "Auteur", author)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Chat_snapshot_decodes_packed_and_unpacked_channels_including_zero_duplicates_and_opaque_slots(bool packed)
    {
        var active = new[] { ChatChannel.General, ChatChannel.Private, ChatChannel.General, ChatChannel.Unknown17 };
        var disabled = new[] { ChatChannel.Trade, ChatChannel.Fight, ChatChannel.Unknown19 };
        byte[] Payload(bool usePacked) => [.. Channels(1, [0, 9, 0, 17], usePacked), .. Channels(2, [5, 11, 19], usePacked)];
        AssertChannels(new(active, disabled), _codec.Decode(CodecTests.Frame(1, "kto", Payload(packed))).Content);
        Event("kto", Payload(true), new ChatChannelsEnabledEvent(active, disabled));
        Event("kto", [], new ChatChannelsEnabledEvent([], []));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(11, false)]
    [InlineData(17, true)]
    [InlineData(19, false)]
    public void Channel_updates_pin_enum_numbers_and_default_zero_omission(int channel, bool enabled)
        => Event("ktq", [.. enabled ? Var(2, 1) : Array.Empty<byte>(), .. channel != 0 ? Var(3, channel) : Array.Empty<byte>()],
            new ChannelUpdateEvent(enabled, (ChatChannel)channel));

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(0)]
    [InlineData(358)]
    [InlineData(int.MaxValue)]
    public void Calendar_uses_the_reviewed_date_id_scalar(int date)
        => Event("lqd", date == 0 ? [] : Var(1, date), new AlmanachCalendarDateEvent(date));

    [Theory]
    [InlineData(long.MaxValue, true)]
    [InlineData(long.MinValue, false)]
    [InlineData(0, false)]
    public void Guild_presence_keeps_int64_character_and_boolean_defaults(long member, bool online)
        => Event("jhd", [.. member == 0 ? Array.Empty<byte>() : Var(1, member), .. online ? Var(2, 1) : Array.Empty<byte>()],
            new GuildMemberOnlineStatusEvent(member, online));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Known_zaaps_accept_packed_and_unpacked_int64_ids_and_keep_order_duplicates_and_empty(bool packed)
    {
        long[] maps = [0, 9_000_000_001, 71_001, 9_000_000_001, long.MaxValue, long.MinValue];
        var frame = _codec.Decode(CodecTests.Frame(1, "hjk", Channels(1, maps, packed)));
        Assert.Equal(maps, Assert.IsType<ZaapKnownListEvent>(frame.Content).Destinations);
        Event("hjk", Channels(1, maps, true), new ZaapKnownListEvent(maps));
        Event("hjk", [], new ZaapKnownListEvent([]));
    }

    [Fact]
    public void Historical_45_destination_fixture_retains_its_182_bytes()
    {
        // Retained from ConnectionProtocolSelfTest: the recorded account's ordered
        // list is a compatibility fixture, never a production destination source.
        long[] maps = [84806401, 154642, 202899464, 54172969, 68552706, 185860609, 68419587,
            126094107, 28050436, 153879813, 120062979, 153880064, 154010371, 212600323,
            179306497, 212861955, 95422468, 179831296, 88085249, 20973313, 88212481,
            147590153, 88212746, 197920772, 156762120, 88082704, 171967506, 156240386,
            88213271, 173278210, 223349001, 165153537, 14419207, 164364304, 115083777,
            165152263, 73400320, 115737095, 207619076, 191105026, 54172489, 99615238,
            142087694, 100270593, 108789760];
        var payload = Convert.FromHexString("0ab3018196b82892b8098880e060a9baea198290d8208184d0588380d0209b96903c8488b00d858a"
            + "b0498388a039808cb0498386b849838cb0658180c0558388c0658490c02d8084e05581a6802a81"
            + "8e800a8188882a8998b0468a8a882a8490b05e8880e04a9092802a928880528294c04a978e882a"
            + "8288d0528992c06a8196e04e878af0069080b04e8194f036878ce04e8080802387849837848880"
            + "638290905bc9b6ea198684c02f8eace0438184e82f8080f033");
        Assert.Equal(182, payload.Length);
        Event("hjk", payload, new ZaapKnownListEvent(maps));
    }

    [Theory]
    [InlineData(-17)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public void Subscription_request_preserves_kind_and_signed_uid_while_outgoing_state_is_uncorrelated(int uid)
    {
        var payload = new byte[] { 0x0a, 2, 0, 9, 0x12, 1, 11 };
        var frame = _codec.Decode(CodecTests.Frame(2, "ktn", payload, uid));
        Assert.Equal(uid, frame.Uid);
        var request = Assert.IsType<ChatChannelSubscriptionRequest>(frame.Content);
        Assert.Equal(new[] { ChatChannel.General, ChatChannel.Private }, request.ActiveChannels);
        Assert.Equal(new[] { ChatChannel.Fight }, request.DisabledChannels);
        var envelope = _codec.ReadEnvelope(_codec.Encode(new(request, uid)));
        Assert.Equal(GameMessageKind.Request, envelope.Kind);
        Assert.Equal(uid, envelope.Uid);
        Assert.Equal(payload, envelope.Content.Value.ToByteArray());
    }

    private static GameEvent Notice(string alias, SocialNoticeInformation? information)
        => alias == "mcm" ? new AllianceMotdEvent(information) : new AllianceBulletinEvent(information);

    private void Event(string alias, byte[] payload, GameEvent expected)
    {
        var decoded = _codec.Decode(CodecTests.Frame(1, alias, payload));
        Assert.Null(decoded.Uid);
        switch (expected)
        {
            case ChatChannelsEnabledEvent channels: AssertChannels(channels, decoded.Content); break;
            case ZaapKnownListEvent zaaps:
                Assert.Equal(zaaps.Destinations, Assert.IsType<ZaapKnownListEvent>(decoded.Content).Destinations); break;
            default: Assert.Equal(expected, decoded.Content); break;
        }
        var envelope = _codec.ReadEnvelope(_codec.Encode(new(expected)));
        Assert.Equal(GameMessageKind.Event, envelope.Kind);
        Assert.Null(envelope.Uid);
        Assert.Equal(alias, envelope.Alias);
        Assert.Equal(payload, envelope.Content.Value.ToByteArray());
    }
    private static void AssertChannels(ChatChannelsEnabledEvent expected, GamePayload payload)
    {
        var actual = Assert.IsType<ChatChannelsEnabledEvent>(payload);
        Assert.Equal(expected.ActiveChannels, actual.ActiveChannels);
        Assert.Equal(expected.DisabledChannels, actual.DisabledChannels);
    }
    private static byte[] Channels(int field, long[] values, bool packed) => packed
        ? Msg(field, Write(writer => { foreach (long value in values) writer.WriteInt64(value); }))
        : values.SelectMany(value => Var(field, value)).ToArray();
    private static byte[] Var(int field, long value) => Write(writer => { writer.WriteTag(field, WireFormat.WireType.Varint); writer.WriteInt64(value); });
    private static byte[] Text(int field, string value) => Msg(field, System.Text.Encoding.UTF8.GetBytes(value));
    private static byte[] Msg(int field, byte[] value) => Write(writer => { writer.WriteTag(field, WireFormat.WireType.LengthDelimited); writer.WriteBytes(ByteString.CopyFrom(value)); });
    private static byte[] Write(Action<CodedOutputStream> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new CodedOutputStream(stream, leaveOpen: true)) write(writer);
        return stream.ToArray();
    }
}
