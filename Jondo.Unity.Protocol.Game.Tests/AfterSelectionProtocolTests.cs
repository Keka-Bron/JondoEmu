using Google.Protobuf;
using Jondo.Unity.Protocol.Game.Wire;
using Xunit;

namespace Jondo.Unity.Protocol.Game.Tests;

public sealed class AfterSelectionProtocolTests
{
    private readonly GameCodec _codec = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Spouse_status_uses_its_boolean_and_has_no_response_uid(bool married)
        => Event("hnv", married ? [8, 1] : [], new SpouseStatusEvent(married));

    [Theory]
    [InlineData(0L)]
    [InlineData(9_000_000_001L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void Storage_balance_is_int64_and_independent_of_combined_currency(long kamas)
        => Event("ivo", kamas == 0 ? [] : Var(1, kamas), new StorageKamasUpdateEvent(kamas));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(14109, 25043)]
    [InlineData(int.MaxValue, int.MinValue)]
    public void Weight_and_capacity_have_distinct_int32_fields(int weight, int capacity)
        => Event("iun", [.. weight == 0 ? Array.Empty<byte>() : Var(1, weight),
            .. capacity == 0 ? Array.Empty<byte>() : Var(3, capacity)], new InventoryWeightEvent(weight, capacity));

    [Fact]
    public void Loading_complete_is_an_empty_event()
        => Event("kvx", [], new CharacterLoadingCompleteEvent());

    [Fact]
    public void Empty_and_present_empty_tag_information_have_distinct_nested_presence()
    {
        Event("hlm", [], new TagStoragesRefreshEvent([]));
        Event("hlm", [0x12, 0], new TagStoragesRefreshEvent([new(null, null)]));
        Event("hlm", [0x12, 4, 0x0a, 0, 0x12, 0],
            new TagStoragesRefreshEvent([new(new([]), new("", "", 0))]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Populated_tags_preserve_order_duplicates_full_int32_and_packed_or_unpacked_input(bool packed)
    {
        int[] ids = [71_001, 0, int.MaxValue, int.MinValue, 71_001];
        byte[] Payload(bool pack) => Msg(2, [.. Msg(1, Integers(1, ids, pack)),
            .. Msg(2, [.. Text(1, "tag-uuid"), .. Text(2, "Équipement ☀"), .. Var(3, -17)])]);
        var expected = new TagStoragesRefreshEvent([new(new(ids), new("tag-uuid", "Équipement ☀", -17))]);
        AssertTags(expected, Assert.IsType<TagStoragesRefreshEvent>(_codec.Decode(CodecTests.Frame(1, "hlm", Payload(packed))).Content));
        Event("hlm", Payload(true), expected);
    }

    [Fact]
    public void Historical_single_piece_set_keeps_empty_effects_and_its_recorded_identifier()
        => Event("iwd", Convert.FromHexString("10b8071a038c9901"), new SetUpdateEvent([], 952, [19596]));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Set_updates_preserve_effect_value_presence_identifiers_and_packed_or_unpacked_input(bool packed)
    {
        int[] ids = [81_001, 81_002, 81_001, int.MaxValue, int.MinValue];
        byte[] Payload(bool pack) => [.. Msg(1, Var(11, 91_001)),
            .. Msg(1, [.. Var(4, 0), .. Var(11, 91_002)]),
            .. Msg(1, [.. Var(4, -23), .. Var(11, 91_003)]),
            .. Var(2, 51_001), .. Integers(3, ids, pack)];
        var expected = new SetUpdateEvent([new(91_001, new ObjectEffectCaseNone()),
            new(91_002, new ObjectEffectCaseValueInt(0)), new(91_003, new ObjectEffectCaseValueInt(-23))], 51_001, ids);
        AssertSet(expected, Assert.IsType<SetUpdateEvent>(_codec.Decode(CodecTests.Frame(1, "iwd", Payload(packed))).Content));
        Event("iwd", Payload(true), expected);
        Event("iwd", Var(2, 51_001), new SetUpdateEvent([], 51_001, []));
    }

    [Fact]
    public void Bonuses_keep_every_reviewed_numeric_kind_signed_values_duplicates_and_empty_replacements()
    {
        Event("lal", [], new BonusesEvent([]));
        // All 37 kinds are reviewed opaque identities. This fixture assigns no gameplay meanings.
        for (int kind = 0; kind <= 36; kind++)
        {
            int[] values = [int.MinValue, 0, int.MaxValue, -17, -17];
            var payload = values.SelectMany(value => Msg(1, [.. value == 0 ? Array.Empty<byte>() : Var(1, value),
                .. kind == 0 ? Array.Empty<byte>() : Var(3, kind)])).ToArray();
            Event("lal", payload, new BonusesEvent(values.Select(value => new Bonus(value, (BonusType)kind)).ToArray()));
        }
        Assert.Throws<GameConversionException>(() => _codec.Encode(new(new BonusesEvent([new(1, (BonusType)37)]))));
    }

    [Theory]
    [InlineData("hnv", "08011063")]
    [InlineData("ivo", "08071200")]
    [InlineData("hlm", "08011200")]
    [InlineData("iun", "08071201611809")]
    public void Raw_decoding_retains_unmapped_populated_fields_that_semantic_construction_does_not_invent(string alias, string hex)
    {
        var payload = Convert.FromHexString(hex);
        Assert.Equal(payload, _codec.DecodeRawPayload(alias, payload).ToByteArray());
        Assert.NotEqual(payload, _codec.ReadEnvelope(_codec.Encode(_codec.Decode(CodecTests.Frame(1, alias, payload)))).Content.Value.ToByteArray());
    }

    private void Event(string alias, byte[] payload, GameEvent expected)
    {
        var frame = _codec.Decode(CodecTests.Frame(1, alias, payload));
        Assert.Null(frame.Uid);
        switch (expected)
        {
            case SetUpdateEvent set: AssertSet(set, Assert.IsType<SetUpdateEvent>(frame.Content)); break;
            case TagStoragesRefreshEvent tags: AssertTags(tags, Assert.IsType<TagStoragesRefreshEvent>(frame.Content)); break;
            case BonusesEvent bonuses:
                Assert.Equal(bonuses.Bonuses, Assert.IsType<BonusesEvent>(frame.Content).Bonuses); break;
            default: Assert.Equal(expected, frame.Content); break;
        }
        var envelope = _codec.ReadEnvelope(_codec.Encode(new(expected)));
        Assert.Equal(GameMessageKind.Event, envelope.Kind);
        Assert.Null(envelope.Uid);
        Assert.Equal(alias, envelope.Alias);
        Assert.Equal(payload, envelope.Content.Value.ToByteArray());
    }
    private static void AssertSet(SetUpdateEvent expected, SetUpdateEvent actual)
    {
        Assert.Equal(expected.SetId, actual.SetId);
        Assert.Equal(expected.ObjectsUid, actual.ObjectsUid);
        Assert.Equal(expected.Effects, actual.Effects);
    }
    private static void AssertTags(TagStoragesRefreshEvent expected, TagStoragesRefreshEvent actual)
    {
        Assert.Equal(expected.TagInformation.Count, actual.TagInformation.Count);
        for (int i = 0; i < expected.TagInformation.Count; i++)
        {
            Assert.Equal(expected.TagInformation[i].TagStorageData, actual.TagInformation[i].TagStorageData);
            Assert.Equal(expected.TagInformation[i].TagObjectData?.TagObjectUuids, actual.TagInformation[i].TagObjectData?.TagObjectUuids);
        }
    }
    private static byte[] Integers(int field, int[] values, bool packed) => packed
        ? Msg(field, Write(writer => { foreach (int value in values) writer.WriteInt32(value); }))
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
