using Google.Protobuf;
using Jondo.Unity.Protocol.Game.Wire;
using Xunit;

namespace Jondo.Unity.Protocol.Game.Tests;

public sealed class PresetListContractTests
{
    private readonly GameCodec _codec = new();

    [Fact]
    public void Populated_list_preserves_independent_keys_recursive_looks_columns_effects_and_spell_slots()
    {
        byte[] child = Body(Var(2, 3), Var(3, 5079));
        byte[] equipmentLook = Body(Packed(1, 0x01112233), Var(3, 1), Packed(5, 150), Packed(6, 5881, 2004));
        byte[] appearanceLook = Body(Var(2, 3), Var(3, 639), Packed(5, 125), Packed(6, 110, 1791),
            Msg(7, Body(Msg(1, child), Var(3, 2), Var(4, 1))));
        byte[] columns = Body(Var(1, 11), Var(2, 23), Var(3, 37), Var(4, 41), Var(5, 0), Var(6, -53));
        byte[] sheet = Body(IntEntry(1, 0, columns), IntEntry(1, 10, Var(6, 79)));
        byte[] item = Body(Var(1, 70), Var(2, int.MaxValue),
            Msg(3, Body(Var(4, 0), Var(11, 118))),
            Msg(3, Body(Str(1, "Ada"), Var(11, 988))),
            Msg(3, Body(Msg(6, Body(Var(1, 5), Var(2, 8_000_000_007), Var(3, 19))), Var(11, 91_011))));
        byte[] spells = Body(IntEntry(2, 0, Packed(1, 0, 18)), IntEntry(2, 91_001, []));
        byte[] preset = Body(Msg(1, Body(Msg(1, equipmentLook), Msg(3, appearanceLook))), Var(2, 17), Msg(3, sheet),
            Str(4, "Distinct build"), IntEntry(5, 0, item), Str(6, "2026-08-09T18:20:27.527350937Z"), Msg(7, spells),
            Var(8, 1), Str(9, "outfit-key"), Str(10, "inner-uuid"));
        byte[] body = StringEntry(1, "outer-key", preset);
        var expected = new CharacterPresetInfo(new(new([0x01112233], 0, 1, [150], [5881, 2004], []),
                new([], 3, 639, [125], [110, 1791], [new(new([], 3, 5079, [], [], []), 2, SubEntityBindingPointCategory.HookPointCategoryPet)])),
            17, new(new Dictionary<int, PresetCharacteristicsDetails> { [0] = new(11, 23, 37, 41, 0, -53), [10] = new(0, 0, 0, 0, null, 79) }),
            "Distinct build", new Dictionary<int, PresetEquipment> { [0] = new(70, int.MaxValue,
                [new(118, new ObjectEffectCaseValueInt(0)), new(988, new ObjectEffectCaseValueString("Ada")),
                    new(91_011, new ObjectEffectCaseValueDice(new(5, 8_000_000_007, 19)))]) },
            "2026-08-09T18:20:27.527350937Z", new(new Dictionary<int, SpellPositions> { [0] = new([0, 18]), [91_001] = new([]) }),
            true, "outfit-key", "inner-uuid");
        AssertBody(body, new(new Dictionary<string, CharacterPresetInfo> { ["outer-key"] = expected }, new Dictionary<string, ForgettableSpellPresetInfo>()));

        var entry = Assert.Single(Decode(body).CharacterPresetInfoById);
        Assert.Equal("outer-key", entry.Key);
        Assert.Equal("inner-uuid", entry.Value.Uuid);
        Assert.Equal(expected.LastModificationDate, entry.Value.LastModificationDate);
        Assert.Equal(expected.CharacteristicsInfo!.CharacteristicDetailsById, entry.Value.CharacteristicsInfo!.CharacteristicDetailsById);
        Assert.Equal(0, entry.Value.PresetLook!.LookWithoutOutfit!.LookType);
        Assert.Equal(5079, Assert.Single(entry.Value.PresetLook.LookWithOutfit!.SubEntities).SubEntityLook.Bones);
        Assert.Equal(expected.EquipmentByPosition[0].Effects, entry.Value.EquipmentByPosition[0].Effects);
        Assert.Equal(int.MaxValue, entry.Value.EquipmentByPosition[0].Uid);
        Assert.Equal(new[] { 0, 18 }, entry.Value.SpellPreset!.SpellPositionsById[0].Positions);
        Assert.Empty(entry.Value.SpellPreset.SpellPositionsById[91_001].Positions);
        Assert.True(entry.Value.IsFavorite);
        Assert.Equal("outfit-key", entry.Value.OutfitUuid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(123)]
    public void Optional_item_uid_and_characteristic_limit_preserve_absence_and_zero(int? optional)
    {
        byte[] item = optional is { } uid ? Var(2, uid) : [];
        byte[] columns = optional is { } limit ? Var(5, limit) : [];
        byte[] body = StringEntry(1, "key", Body(Msg(3, IntEntry(1, 0, columns)), IntEntry(5, 0, item)));
        var expected = EmptyPreset() with
        {
            CharacteristicsInfo = new(new Dictionary<int, PresetCharacteristicsDetails> { [0] = new(0, 0, 0, 0, optional, 0) }),
            EquipmentByPosition = new Dictionary<int, PresetEquipment> { [0] = new(0, optional, []) },
        };
        AssertBody(body, List(expected));
        var decoded = Decode(body).CharacterPresetInfoById["key"];
        Assert.Equal(optional, decoded.CharacteristicsInfo!.CharacteristicDetailsById[0].Limit);
        Assert.Equal(optional, decoded.EquipmentByPosition[0].Uid);
    }

    [Fact]
    public void Empty_replacement_and_absent_versus_present_empty_wrappers_remain_distinct()
    {
        AssertBody([], new(new Dictionary<string, CharacterPresetInfo>(), new Dictionary<string, ForgettableSpellPresetInfo>()));
        Assert.Empty(Decode([]).CharacterPresetInfoById);
        AssertBody(StringEntry(1, "key", []), List(EmptyPreset()));
        AssertBody(StringEntry(1, "key", Body(Msg(1, []), Msg(3, []), Msg(7, []))), List(EmptyPreset() with
        {
            PresetLook = new(null, null), CharacteristicsInfo = new(new Dictionary<int, PresetCharacteristicsDetails>()),
            SpellPreset = new(new Dictionary<int, SpellPositions>()),
        }));
        var absent = Decode(StringEntry(1, "key", [])).CharacterPresetInfoById["key"];
        Assert.Null(absent.PresetLook);
        Assert.Null(absent.CharacteristicsInfo);
        Assert.Null(absent.SpellPreset);
    }

    [Fact]
    public void Historical_explicit_zero_keys_decode_and_normalize_without_losing_zero_positions()
    {
        byte[] legacy = StringEntry(1, "key", Body(Msg(3, Msg(1, Body(Var(1, 0), Msg(2, [])))),
            Msg(5, Body(Var(1, 0), Msg(2, Var(2, 0)))), Msg(7, Msg(2, Body(Var(1, 0), Msg(2, Packed(1, 0)))))));
        byte[] normalized = StringEntry(1, "key", Body(Msg(3, IntEntry(1, 0, [])), IntEntry(5, 0, Var(2, 0)), Msg(7, IntEntry(2, 0, Packed(1, 0)))));
        var decoded = Decode(legacy);
        var entry = decoded.CharacterPresetInfoById["key"];
        Assert.Contains(0, entry.CharacteristicsInfo!.CharacteristicDetailsById.Keys);
        Assert.Equal(0, entry.EquipmentByPosition[0].Uid);
        Assert.Equal(new[] { 0 }, entry.SpellPreset!.SpellPositionsById[0].Positions);
        AssertBody(normalized, decoded);
    }

    [Fact]
    public void Type_only_forgettable_values_retain_map_keys_but_do_not_copy_unmapped_children()
    {
        byte[] historical = StringEntry(2, "forgettable-key", Body(Var(1, 17), Msg(2, IntEntry(2, 91_001, Packed(1, 3))), Str(6, "original")));
        byte[] mapped = StringEntry(2, "forgettable-key", []);
        Assert.NotEqual(_codec.DecodeRawPayload("ihb", mapped), _codec.DecodeRawPayload("ihb", historical));
        var decoded = Decode(historical);
        Assert.Equal("forgettable-key", Assert.Single(decoded.ForgettableSpellPresetInfoById).Key);
        AssertBody(mapped, decoded);
    }

    private static CharacterPresetInfo EmptyPreset() => new(null, 0, null, "", new Dictionary<int, PresetEquipment>(), "", null, false, "", "");
    private static PresetListEvent List(CharacterPresetInfo preset) => new(new Dictionary<string, CharacterPresetInfo> { ["key"] = preset }, new Dictionary<string, ForgettableSpellPresetInfo>());
    private PresetListEvent Decode(byte[] body) => Assert.IsType<PresetListEvent>(_codec.Decode(CodecTests.Frame(1, "ihb", body)).Content);
    private void AssertBody(byte[] expected, PresetListEvent message)
    {
        var envelope = _codec.ReadEnvelope(_codec.Encode(new(message)));
        Assert.Equal("ihb", envelope.Alias);
        Assert.Equal(GameMessageKind.Event, envelope.Kind);
        Assert.Null(envelope.Uid);
        Assert.Equal(expected, envelope.Content.Value.ToByteArray());
    }
    // The installed repeated-entry wire contracts omit default scalar keys. A
    // missing key still means zero; optional values inside the entry stay present.
    private static byte[] IntEntry(int tag, int key, byte[] value) => Msg(tag, Body(key == 0 ? [] : Var(1, key), Msg(2, value)));
    private static byte[] StringEntry(int tag, string key, byte[] value) => Msg(tag, Body(Str(1, key), Msg(2, value)));
    private static byte[] Body(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
    private static byte[] Var(int tag, long value) => Write(output => { output.WriteTag(tag, WireFormat.WireType.Varint); output.WriteInt64(value); });
    private static byte[] Msg(int tag, byte[] value) => Write(output => { output.WriteTag(tag, WireFormat.WireType.LengthDelimited); output.WriteBytes(ByteString.CopyFrom(value)); });
    private static byte[] Str(int tag, string value) => Write(output => { output.WriteTag(tag, WireFormat.WireType.LengthDelimited); output.WriteString(value); });
    private static byte[] Packed(int tag, params int[] values) => Msg(tag, Write(output => { foreach (int value in values) output.WriteInt32(value); }));
    private static byte[] Write(Action<CodedOutputStream> write)
    {
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, leaveOpen: true)) write(output);
        return stream.ToArray();
    }
}
