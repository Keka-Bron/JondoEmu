using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.World;

// Mounts.Initialize publishes shared tables; keep these database-backed cases isolated.
[CollectionDefinition("Character look configurations", DisableParallelization = true)]
public sealed class CharacterLookConfigurationCollection;

[Collection("Character look configurations")]
public sealed class CharacterLookConfigurationTests : IDisposable
{
    private const long Account = 8_900_000_189;
    private const int Cape = 958, CapeSkin = 2004, AppearanceCape = 18579, Pet = 20325;
    private readonly GameSession session = GameSession.SinSocket();
    private readonly IDisposable context;
    private readonly DbCharacter character;

    public CharacterLookConfigurationTests()
    {
        Wardrobe.Initialize();
        Mounts.Initialize();
        long id = DatabaseManager.CreateCharacter(Account, 1, "Look-Configurations", 11, 0,
            HeadTable.DefaultHeadId(11, 0), [0x112233, 0, 0xFFFFFF, 4, 5, 6],
            191105026, 50, 0, 0, Array.Empty<(int, int)>());
        Assert.True(id > 0);
        character = DatabaseManager.GetCharacterById(id)!;
        session.State.CharacterId = id;
        context = SessionContext.Push(session);
        try
        {
            Wardrobe.SaveBody(id, 110); // Retro Sacrier, skin 5881, instead of the breed's skin 110.
            Wardrobe.SaveAura(id, 22);
            CharacterSize.Set(id, 150);
            Wear(Cosmetics.SlotCape, AppearanceCape);
            Wear(Cosmetics.SlotPet, Pet);
            Equip(7, Cape);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    [Fact]
    public void Mounted_world_and_preview_keep_attachments_on_the_root_in_their_original_order()
    {
        Wear(Cosmetics.SlotMount, 15861);
        Assert.Equal(1, CharacterLookBuilder.SemanticMapLook(character).Bones);
        Equip(Mounts.Slot, 7807);

        var world = Parse(CharacterLookBuilder.MapLook(character));
        Assert.Equal(3025, Bone(world));
        Assert.Equal(new long[] { 6, 2 }, Children(world).Select(child => Number(child, 4)));
        // The power aura (22) below level 200 is its level 100 bones.
        Assert.Equal(Cosmetics.PowerAuraBelow200Bones, Bone(ChildLook(Children(world)[0])));
        var rider = ChildLook(Children(world)[1]);
        Assert.Equal(2, Bone(rider));
        AssertBody(rider, 5881, [1791, 3494]);

        var preview = Parse(CharacterLookBuilder.BuildLook(character, paraLaVentana: true));
        Assert.Equal(new long[] { 1, 2 }, Children(preview).Select(child => Number(child, 4)));
        var pet = ChildLook(Children(preview)[0]);
        Assert.Equal(5079, Bone(pet));
        Assert.Equal(Packed(rider, 1), Packed(pet, 1));
        Assert.Empty(Children(ChildLook(Children(preview)[1])));

        var onFoot = Parse(CharacterLookBuilder.BuildLook(character, onFoot: true));
        Assert.Equal(1, Bone(onFoot));
        Assert.Equal(new long[] { 1, 6 }, Children(onFoot).Select(child => Number(child, 4)));
        AssertBody(onFoot, 5881, [1791, 3494]);

        Wardrobe.SetHidden(character.Id, Cosmetics.SlotMount, true);
        Assert.Equal(639, Bone(Parse(CharacterLookBuilder.MapLook(character))));
    }

    [Fact]
    public void Bare_presets_and_portrait_share_body_colors_and_size_but_select_different_clothes()
    {
        var bare = Parse(CharacterLookBuilder.BareLook(character));
        AssertBody(bare, 5881, []);
        Assert.Empty(Children(bare));

        var (equipmentBytes, appearanceBytes) = CharacterLookBuilder.PresetLooks(character, [(7, Cape)]);
        var equipment = Parse(equipmentBytes);
        var appearance = Parse(appearanceBytes);
        AssertBody(equipment, 5881, [CapeSkin]);
        AssertBody(appearance, 5881, [1791, 3494]);
        Assert.DoesNotContain(equipment.Fields, field => field.FieldNumber == 2);
        Assert.Equal(3, Number(appearance, 2));
        Assert.Empty(Children(equipment));
        Assert.Empty(Children(appearance));
        var (semanticEquipment, semanticAppearance) = CharacterLookBuilder.SemanticPresetLooks(character, [(7, Cape)]);
        Assert.Equal(0, semanticEquipment.LookType);
        Assert.Equal(3, semanticAppearance.LookType);
        Assert.Equal(new[] { 5881, HeadTable.SkinFor(character.HeadId, character.Breed, character.Sex), CapeSkin }, semanticEquipment.Skins);
        Assert.Equal(new[] { 5881, HeadTable.SkinFor(character.HeadId, character.Breed, character.Sex), 1791, 3494 }, semanticAppearance.Skins);
        Assert.Equal(semanticEquipment.Colors, semanticAppearance.Colors);
        Assert.Equal(new[] { 82 }, semanticEquipment.Scales);
        Assert.Empty(semanticEquipment.SubEntities);
        Assert.Empty(semanticAppearance.SubEntities);

        string colors = "1=#112233,2=#000000,3=#FFFFFF,4=#000004,5=#000005,6=#000006";
        Assert.Equal($"{{1|{string.Join(",", Packed(appearance, 6))}|{colors}|{Packed(bare, 5)[0]}}}",
            CharacterLookBuilder.Drawable(character));

        var (emptyEquipment, _) = CharacterLookBuilder.PresetLooks(character, []);
        AssertBody(Parse(emptyEquipment), 5881, []); // Explicit empty equipment must not read live gear.
    }

    [Fact]
    public void Hidden_cosmetics_cover_real_equipment_without_adding_their_own_skins()
    {
        Wardrobe.SetHidden(character.Id, Cosmetics.SlotCape, true);
        var (_, appearance) = CharacterLookBuilder.PresetLooks(character, [(7, Cape)]);
        AssertBody(Parse(appearance), 5881, []);

        Wardrobe.SetHidden(character.Id, Cosmetics.SlotPet, true);
        Assert.Equal(new long[] { 6 }, Children(Parse(CharacterLookBuilder.MapLook(character)))
            .Select(child => Number(child, 4)));
    }

    [Theory]
    [InlineData(1, 1136)]
    [InlineData(20, 1155)]
    public void Living_garments_use_the_encoded_variant_while_covering_the_equipment(int variant, int skin)
    {
        const int livingCape = 9234;
        var look = CharacterLookBuilder.Build(character, CharacterLookPurpose.PresetAppearance, new()
        {
            Equipment = [(7, Cape)],
            Garments = [new(Cosmetics.SlotCape, livingCape * 1000L + variant, livingCape, false)],
        });
        Assert.Equal(new[] { 5881, HeadTable.SkinFor(character.HeadId, character.Breed, character.Sex), skin }, look.Skins);
    }

    [Fact]
    public void Explicit_empty_overrides_remove_clothes_and_colors_without_inheriting_live_state()
    {
        var look = CharacterLookBuilder.Build(character, CharacterLookPurpose.SavedOutfit, new()
        {
            BodyId = 0, Equipment = [], Garments = [], Colors = [],
        });
        Assert.Equal(new[] { 110, HeadTable.SkinFor(character.HeadId, character.Breed, character.Sex) }, look.Skins);
        Assert.Empty(look.Colors);
        Assert.Empty(look.SubEntities);
        Assert.Equal(5881, CharacterLookBuilder.Build(character, CharacterLookPurpose.Portrait).Skins[0]);

        // The byte adapter keeps the older outfit API's empty-palette defaulting convention.
        var outfit = Parse(CharacterLookBuilder.OutfitLook(character, character.HeadId, 0, [], []));
        Assert.Equal(CharacterLookBuilder.IndexedColors(character.Breed, character.Sex),
            Packed(outfit, 1).Select(color => (long)color));
    }

    [Fact]
    public void Explicit_owner_resolution_does_not_take_equipment_or_wardrobe_from_another_session()
    {
        var wrongSession = GameSession.SinSocket();
        wrongSession.State.CharacterId = character.Id + 100_000;
        wrongSession.State.EquipmentItems[1] = new Equipment.Item { Uid = 1, Template = 6988, Position = 6 };
        using var wrongContext = SessionContext.Push(wrongSession);

        var look = Parse(CharacterLookBuilder.MapLook(character));

        AssertBody(look, 5881, [1791, 3494]);
        Assert.Equal(new long[] { 1, 6 }, Children(look).Select(child => Number(child, 4)));
        Assert.Same(wrongSession, SessionContext.Actual);
    }

    [Fact]
    public void Saved_outfit_overrides_body_head_palette_and_garments_but_keeps_current_equipment_and_size()
    {
        using var heads = JsonDocument.Parse(File.ReadAllText(Paths.HeadsJson));
        var head = heads.RootElement.GetProperty("heads").EnumerateObject().First(entry =>
            entry.Value.GetProperty("breed").GetInt32() == character.Breed &&
            entry.Value.GetProperty("gender").GetInt32() == character.Sex &&
            int.Parse(entry.Name) != character.HeadId);
        long[] colors = [0x01010203, 0x02040506];
        Wardrobe.Worn[] garments = [new(Cosmetics.SlotPet, Pet * 1000L, Pet, false)];
        var outfit = Parse(CharacterLookBuilder.OutfitLook(character, int.Parse(head.Name),
            BodyTable.BaseOf(character.Breed, character.Sex), colors, garments));

        Assert.Equal(new[] { 110, head.Value.GetProperty("skin").GetInt32(), CapeSkin }, Packed(outfit, 6));
        Assert.Equal(colors, Packed(outfit, 1).Select(value => (long)value));
        Assert.Equal(Packed(Parse(CharacterLookBuilder.BareLook(character)), 5), Packed(outfit, 5));
        var pet = Assert.Single(Children(outfit));
        Assert.Equal(1, Number(pet, 4));
        Assert.Equal(Packed(outfit, 1), Packed(ChildLook(pet), 1));
    }

    [Fact]
    public void Map_replacements_prioritize_phantom_then_mutation_then_disguise_and_previews_ignore_them()
    {
        Equip(Mutations.Slot, 15019, new Equipment.ItemEffect(Mutations.TransformsInto, 0, 3577, 1));
        Equip(SlotLooks.RoleplayBuffer, 2062, new Equipment.ItemEffect(SlotLooks.ChangesAppearance, 884, 0, 0));
        Assert.NotNull(Mutations.Current());

        Phantoms.Become(character.Id, 1);
        Assert.Equal(3, CharacterLookBuilder.SemanticMapLook(character).Bones);
        Assert.Equal(2554, CharacterLookBuilder.SemanticLook(character).Bones);
        Assert.Equal(1, CharacterLookBuilder.SemanticLook(character, paraLaVentana: true).Bones);
        AssertBody(Parse(CharacterLookBuilder.BareLook(character)), 5881, []);

        Phantoms.Rise(character.Id);
        Assert.Equal(2554, CharacterLookBuilder.SemanticMapLook(character).Bones);
        session.State.EquipmentItems.Remove(session.State.EquipmentItems.Single(item => item.Value.Position == Mutations.Slot).Key);
        Assert.Equal(761, CharacterLookBuilder.SemanticMapLook(character).Bones);
        Assert.Equal(1, CharacterLookBuilder.SemanticLook(character).Bones);
    }

    private void Wear(int slot, int gid) => Wardrobe.Wear(character.Id, slot, gid * 1000L, gid);

    private void Equip(int slot, int gid, params Equipment.ItemEffect[] effects)
    {
        long uid = DatabaseManager.NextItemUid();
        Assert.True(DatabaseManager.InsertCharacterItem(uid, character.Id, gid, 1, slot, "[]"));
        var item = new Equipment.Item { Uid = uid, Template = gid, Position = slot };
        item.Effects.AddRange(effects);
        session.State.EquipmentItems[uid] = item;
    }

    private void AssertBody(ProtoMessage body, int skin, int[] clothing)
    {
        Assert.Equal(new[] { skin, HeadTable.SkinFor(character.HeadId, character.Breed, character.Sex) }
            .Concat(clothing), Packed(body, 6));
        Assert.Equal(new[] { 0x01112233, 0x02000000, 0x03FFFFFF, 0x04000004, 0x05000005, 0x06000006 }, Packed(body, 1));
        Assert.Equal(BreedLookCatalog.Get(character.Breed, character.Sex)!.Scales
            .Select(scale => (int)Math.Round(scale * 1.5)), Packed(body, 5));
    }

    private static ProtoMessage Parse(byte[] bytes) => ProtoMessage.Parse(bytes);
    private static long Number(ProtoMessage message, int tag) => message.Fields.Single(field => field.FieldNumber == tag).VarIntValue;
    private static long Bone(ProtoMessage message) => Number(message, 3);
    private static ProtoMessage[] Children(ProtoMessage message) => message.Fields
        .Where(field => field.FieldNumber == 7).Select(field => Parse(field.BytesValue)).ToArray();
    private static ProtoMessage ChildLook(ProtoMessage child) => Parse(child.Fields.Single(field => field.FieldNumber == 1).BytesValue);

    private static int[] Packed(ProtoMessage message, int tag)
    {
        using var input = new Google.Protobuf.CodedInputStream(message.Fields.Single(field => field.FieldNumber == tag).BytesValue);
        var values = new List<int>();
        while (!input.IsAtEnd) values.Add(input.ReadInt32());
        return values.ToArray();
    }

    public void Dispose()
    {
        Phantoms.Rise(character.Id);
        context.Dispose();
        DatabaseManager.DeleteCharacter(character.Id, Account);
    }
}
