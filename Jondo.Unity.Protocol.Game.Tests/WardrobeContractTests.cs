using Google.Protobuf;
using Jondo.Unity.Protocol.Game.Wire;
using Xunit;

namespace Jondo.Unity.Protocol.Game.Tests;

public sealed class WardrobeContractTests
{
    private readonly GameCodec _codec = new();

    [Fact]
    public void Outfit_lists_preserve_head_id_optional_fields_hidden_slots_objects_and_recursive_looks()
    {
        byte[] child = Body(Var(2, 3), Var(3, 77));
        byte[] look = Body(Packed(1, 0x01112233), Var(2, 3), Var(3, 1), Packed(5, 125), Packed(6, 5881, 2004),
            Msg(7, Body(Msg(1, child), Var(4, 1))));
        byte[] outfit = Body(Msg(1, Packed(2, 0x112233, -1)), Var(2, 1), Str(3, "original-date"), Var(5, 110), Var(6, 1),
            Str(7, "saved"), Var(8, 24), Var(9, 0), Var(10, 77), Var(11, 617), Msg(12, look), Str(13, "Distinct"),
            Packed(14, 10, 9), Var(15, -1), Var(16, 0), Msg(17, Body(Msg(2, Body(Var(1, 0), Var(2, 9256))))));
        var expected = new Outfit(new([0x112233, -1]), true, "original-date", 110, true, "saved", 24, 0, 77, 617,
            new([0x01112233], 3, 1, [125], [5881, 2004], [new(new([], 3, 77, [], [], []), 0, SubEntityBindingPointCategory.HookPointCategoryPet)]),
            "Distinct", [ObjectSlot.Hat, ObjectSlot.Cape], -1, 0, new Dictionary<int, SkinOutfit> { [0] = new(0, 9256) });
        AssertBody("lyt", Body(Msg(1, outfit), Msg(2, outfit)), new OutfitsLoadedEvent([expected], expected));
        var actual = Assert.IsType<OutfitsLoadedEvent>(_codec.Decode(CodecTests.Frame(1, "lyt", Body(Msg(1, outfit), Msg(2, outfit)))).Content);
        Assert.Equal(617, actual.Current!.HeadId);
        Assert.Equal(0, actual.Current.AuraEmoteId);
        Assert.Equal(0, actual.Current.OrnamentId);
        Assert.Equal(0, actual.Current.Objects[0].Skin);
        Assert.Equal(77, Assert.Single(actual.Current.EntityLook!.SubEntities).SubEntityLook.Bones);
        Assert.Equal(new[] { ObjectSlot.Hat, ObjectSlot.Cape }, actual.Current.HiddenSlots);

        var absent = expected with { AuraEmoteId = null, TitleId = null, OrnamentId = null, Objects = new Dictionary<int, SkinOutfit>(), Colors = null, EntityLook = null };
        byte[] absentBody = Body(Var(2, 1), Str(3, "original-date"), Var(5, 110), Var(6, 1), Str(7, "saved"), Var(8, 24),
            Var(11, 617), Str(13, "Distinct"), Packed(14, 10, 9), Var(15, -1));
        AssertBody("lyt", Msg(1, absentBody), new OutfitsLoadedEvent([absent], null));
        AssertBody("lyt", Msg(2, []), new OutfitsLoadedEvent([], new(null, false, "", 0, false, "", 0, null, null, 0, null, "", [], 0, null, new Dictionary<int, SkinOutfit>())));
    }

    [Fact]
    public void Cosmetics_and_ownership_use_independent_populated_wire_fixtures()
    {
        byte[] item = Body(Var(1, 9256), Msg(2, Body(Var(4, 0), Var(11, 118))),
            Msg(2, Body(Str(1, "Ada"), Var(11, 988))), Var(3, 2), Var(4, int.MaxValue));
        AssertBody("lwt", Body(Packed(3, 616, 617), Packed(4, 24), Packed(5, 110), Msg(6, item)),
            new CosmeticInventoryLoadedEvent([616, 617], [24], [110],
                [new(9256, [new(118, new ObjectEffectCaseValueInt(0)), new(988, new ObjectEffectCaseValueString("Ada"))], 2, int.MaxValue, [])]));
        AssertBody("hhy", Body(Packed(1, 31, 17), Packed(2, 135, 42)), new TitlesAndOrnamentsEvent([31, 17], [135, 42]));
        AssertBody("lxc", Body(Str(1, "owned-look"), Msg(2, Body(Var(2, 3), Var(3, 639)))),
            new OutfitLookUpdatedEvent("owned-look", new([], 3, 639, [], [], [])));
    }

    [Fact]
    public void Empty_replacements_notifications_and_optional_look_presence_remain_distinct()
    {
        AssertBody("hhy", [], new TitlesAndOrnamentsEvent([], []));
        AssertBody("lwt", [], new CosmeticInventoryLoadedEvent([], [], [], []));
        AssertBody("lyt", [], new OutfitsLoadedEvent([], null));
        AssertBody("hid", [], new TitleSelectedEvent());
        AssertBody("hif", [], new OrnamentSelectedEvent());
        AssertBody("lym", [], new OutfitAuraChangedEvent(null));
        AssertBody("lym", Var(1, 206), new OutfitAuraChangedEvent(206));
        AssertBody("lxc", Str(1, "id"), new OutfitLookUpdatedEvent("id", null));
        AssertBody("lxc", Body(Str(1, "id"), Msg(2, [])), new OutfitLookUpdatedEvent("id", new([], 0, 0, [], [], [])));
        // Historical populated title scalars remain available through raw inspection;
        // direct construction deliberately omits them under the wardrobe policy.
        byte[] historical = CodecTests.Frame(1, "hid", Var(1, 77));
        Assert.Equal(Var(1, 77), _codec.DecodeRawPayload("hid", Var(1, 77)).ToByteArray());
        Assert.Equal(new TitleSelectedEvent(), _codec.Decode(historical).Content);
    }

    private void AssertBody(string alias, byte[] body, GameEvent message)
    {
        var envelope = _codec.ReadEnvelope(_codec.Encode(new(message)));
        Assert.Equal(alias, envelope.Alias);
        Assert.Equal(GameMessageKind.Event, envelope.Kind);
        Assert.Null(envelope.Uid);
        Assert.Equal(body, envelope.Content.Value.ToByteArray());
        Assert.IsType(message.GetType(), _codec.Decode(CodecTests.Frame(1, alias, body)).Content);
    }
    private static byte[] Body(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
    private static byte[] Var(int tag, long value) => Write(output => { output.WriteTag(tag, WireFormat.WireType.Varint); output.WriteInt64(value); });
    private static byte[] Msg(int tag, byte[] value) => Write(output => { output.WriteTag(tag, WireFormat.WireType.LengthDelimited); output.WriteBytes(ByteString.CopyFrom(value)); });
    private static byte[] Str(int tag, string value) => Msg(tag, System.Text.Encoding.UTF8.GetBytes(value));
    private static byte[] Packed(int tag, params long[] values) => Msg(tag, Write(output => { foreach (long value in values) output.WriteInt64(value); }));
    private static byte[] Write(Action<CodedOutputStream> write)
    {
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, leaveOpen: true)) write(output);
        return stream.ToArray();
    }
}
