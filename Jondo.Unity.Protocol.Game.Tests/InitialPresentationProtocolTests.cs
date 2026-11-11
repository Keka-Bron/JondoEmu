using Google.Protobuf;
using Jondo.Unity.Protocol.Game.Wire;
using Xunit;

namespace Jondo.Unity.Protocol.Game.Tests;

public sealed class InitialPresentationProtocolTests
{
    private readonly GameCodec _codec = new();

    [Fact]
    public void Sequence_aura_and_ornament_are_uncorrelated_empty_events()
    {
        Event("lqg", [], new SequenceNumberEvent());
        Event("lym", [], new OutfitAuraChangedEvent());
        Event("hif", [], new OrnamentSelectedEvent());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    [InlineData(12287)]
    public void Capability_mask_keeps_all_int32_bits(int mask)
        => Event("kvv", mask == 0 ? [] : Var(1, mask), new CharacterCapabilitiesEvent(mask));

    [Fact]
    public void Color_collection_keeps_absent_and_present_empty_blocks_and_distinct_slot_counts()
    {
        Event("lwc", [], new CharacterAppearanceCollectionEvent(null));
        Event("lwc", [0x0a, 0], new CharacterAppearanceCollectionEvent(new(0, [], 0)));
        Event("lwc", Msg(1, [.. Var(1, 2), .. Msg(3, []), .. Var(4, 17)]),
            new CharacterAppearanceCollectionEvent(new(2, [new([])], 17)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Color_palettes_keep_replacement_order_inheritance_full_int32_and_packed_or_unpacked_input(bool packed)
    {
        int[] first = [-1, 0x112233, 0, int.MinValue, int.MaxValue, -1];
        int[] second = [0x556677, 0xabcdef];
        byte[] Payload(bool pack) => Msg(1, [.. Var(1, 3), .. Msg(3, Integers(2, first, pack)),
            .. Msg(3, Integers(2, second, pack)), .. Var(4, 19)]);
        var expected = new CharacterAppearanceCollectionEvent(new(3, [new(first), new(second)], 19));
        AssertColors(expected, Assert.IsType<CharacterAppearanceCollectionEvent>(_codec.Decode(CodecTests.Frame(1, "lwc", Payload(packed))).Content));
        Event("lwc", Payload(true), expected);
    }

    [Fact]
    public void Guild_configuration_binds_every_used_setting_and_int64_cost_and_delay_independently()
    {
        var guild = new GuildConfiguration(new(81_001, 7, 2, 5, 81_002, 97), 9_000_000_001,
            new(8_000_000_001, 11, 23, 37));
        byte[] activity = [.. Var(1, 81_001), .. Var(2, 7), .. Var(3, 2), .. Var(4, 5), .. Var(5, 81_002), .. Var(6, 97)];
        byte[] contribution = [.. Var(1, 8_000_000_001), .. Var(2, 11), .. Var(3, 23), .. Var(4, 37)];
        Event("krj", Msg(2, [.. Msg(1, activity), .. Var(2, 9_000_000_001), .. Msg(3, contribution)]), new ConfigurationEvent(guild));
        Event("krj", [], new ConfigurationEvent(null));
        Event("krj", Msg(2, []), new ConfigurationEvent(new(null, 0, null)));
        Event("krj", Msg(2, [.. Msg(1, []), .. Msg(3, [])]),
            new ConfigurationEvent(new(new(0, 0, 0, 0, 0, 0), 0, new(0, 0, 0, 0))));
    }

    [Fact]
    public void Outfit_look_keeps_identifier_and_recursive_appearance_without_capture_extras()
    {
        byte[] child = [.. Packed(1, [0x02112233]), .. Var(2, 3), .. Var(3, 2), .. Packed(5, [55]), .. Packed(6, [81_001, 81_002])];
        byte[] root = [.. Packed(1, [0x01556677]), .. Var(2, 3), .. Var(3, 71_001), .. Packed(5, [111]),
            .. Msg(7, [.. Msg(1, child), .. Var(3, 7), .. Var(4, 2)])];
        var look = new EntityLook([0x01556677], 3, 71_001, [111], [],
            [new(new([0x02112233], 3, 2, [55], [81_001, 81_002], []), 7, SubEntityBindingPointCategory.HookPointCategoryMountDriver)]);
        Event("lxc", [.. Text(1, "outfit-É"), .. Msg(2, root)], new OutfitLookUpdatedEvent("outfit-É", look));
        Event("lxc", Text(1, "outfit"), new OutfitLookUpdatedEvent("outfit", null));
        Event("lxc", Msg(2, []), new OutfitLookUpdatedEvent("", new([], 0, 0, [], [], [])));
    }

    [Fact]
    public void Unused_indeterminate_values_are_omitted_by_stable_emission_while_raw_inspection_retains_them()
    {
        Check("lym", Var(1, 206), []);
        Check("hif", Var(1, 135), []);
        Check("krj", Msg(1, [.. Var(1, 30), .. Var(2, 5), .. Var(3, 300)]), []);
        Check("krj", Msg(2, Msg(1, Msg(7, Var(1, 91)))), Msg(2, Msg(1, [])));
        Check("lwc", Msg(1, [.. Var(2, 23), .. Msg(3, Packed(1, [17, 29]))]), Msg(1, Msg(3, [])));
        Check("lxc", Msg(3, Var(3, 71_001)), []);

        void Check(string alias, byte[] populated, byte[] omitted)
        {
            Assert.Equal(populated, _codec.DecodeRawPayload(alias, populated).ToByteArray());
            var projected = _codec.Decode(CodecTests.Frame(1, alias, populated));
            var envelope = _codec.ReadEnvelope(_codec.Encode(projected));
            Assert.Equal(omitted, envelope.Content.Value.ToByteArray());
            Assert.Equal(GameMessageKind.Event, envelope.Kind);
            Assert.Null(envelope.Uid);
        }
    }

    private void Event(string alias, byte[] payload, GameEvent expected)
    {
        var frame = _codec.Decode(CodecTests.Frame(1, alias, payload));
        Assert.Null(frame.Uid);
        switch (expected)
        {
            case CharacterAppearanceCollectionEvent colors: AssertColors(colors, Assert.IsType<CharacterAppearanceCollectionEvent>(frame.Content)); break;
            case OutfitLookUpdatedEvent look:
                Assert.Equal(System.Text.Json.JsonSerializer.Serialize(look), System.Text.Json.JsonSerializer.Serialize(Assert.IsType<OutfitLookUpdatedEvent>(frame.Content))); break;
            default: Assert.Equal(expected, frame.Content); break;
        }
        var envelope = _codec.ReadEnvelope(_codec.Encode(new(expected)));
        Assert.Equal(alias, envelope.Alias);
        Assert.Equal(GameMessageKind.Event, envelope.Kind);
        Assert.Null(envelope.Uid);
        Assert.Equal(payload, envelope.Content.Value.ToByteArray());
    }
    private static void AssertColors(CharacterAppearanceCollectionEvent expected, CharacterAppearanceCollectionEvent actual)
    {
        if (expected.CharacterColors == null) { Assert.Null(actual.CharacterColors); return; }
        Assert.NotNull(actual.CharacterColors);
        Assert.Equal(expected.CharacterColors.SlotsUnlocked, actual.CharacterColors.SlotsUnlocked);
        Assert.Equal(expected.CharacterColors.SlotsTotal, actual.CharacterColors.SlotsTotal);
        Assert.Equal(expected.CharacterColors.ColorPalettes.Count, actual.CharacterColors.ColorPalettes.Count);
        for (int i = 0; i < expected.CharacterColors.ColorPalettes.Count; i++)
            Assert.Equal(expected.CharacterColors.ColorPalettes[i].Colors, actual.CharacterColors.ColorPalettes[i].Colors);
    }
    private static byte[] Integers(int field, int[] values, bool packed) => packed ? Packed(field, values)
        : values.SelectMany(value => Var(field, value)).ToArray();
    private static byte[] Packed(int field, int[] values) => Msg(field, Write(writer => { foreach (int value in values) writer.WriteInt32(value); }));
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
