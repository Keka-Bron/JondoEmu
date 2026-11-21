using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Managers;

/// <summary>
/// Resolves character state into a body, mount and root attachments, then composes one semantic
/// look. Byte messages and launcher portraits are output formats of that same pipeline.
/// Breed defaults belong to <see cref="BreedLookCatalog"/>; stored colors remain plain RGB.
/// </summary>
public static class CharacterLookBuilder
{
    private const int LookType = 3;

    // Equipment slots use different numbering from the appearance window's slots.
    private const int SlotHat = 6;
    private const int SlotCape = 7;
    private const int SlotShield = 15;

    private sealed record LookPolicy(bool Equipment = true, bool Garments = true,
        bool Mount = false, bool Pets = false, bool MountedPets = false, bool Aura = false);

    private sealed record ResolvedLook(EntityLook Body, EntityLook? Mount,
        IReadOnlyList<SubEntityInformation> Attachments);

    private enum LookWireFormat { Standard, PresetEquipment }

    private static LookPolicy PolicyFor(CharacterLookPurpose purpose) => purpose switch
    {
        CharacterLookPurpose.Normal or CharacterLookPurpose.Map => new(Mount: true, Pets: true, Aura: true),
        CharacterLookPurpose.AppearancePreview => new(Mount: true, Pets: true, MountedPets: true),
        CharacterLookPurpose.Bare => new(Equipment: false, Garments: false),
        CharacterLookPurpose.SavedOutfit => new(Pets: true),
        CharacterLookPurpose.PresetEquipment => new(Garments: false),
        CharacterLookPurpose.PresetAppearance or CharacterLookPurpose.Portrait => new(),
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };

    /// <summary>Builds an appearance independently of its eventual wire or portrait format.</summary>
    public static EntityLook Build(DbCharacter character,
        CharacterLookPurpose purpose = CharacterLookPurpose.Normal, CharacterLookOverrides? overrides = null)
    {
        var resolved = Resolve(character, purpose, overrides ?? new());
        return Compose(resolved.Body, resolved.Mount, resolved.Attachments);
    }

    /// <summary>Rebuilds the map appearance, including phantom, mutation and disguise precedence.</summary>
    public static byte[] MapLook(DbCharacter character)
        => WriteLook(Build(character, CharacterLookPurpose.Map)).Build();

    public static EntityLook SemanticMapLook(DbCharacter character)
        => Build(character, CharacterLookPurpose.Map);

    public static byte[] BuildLook(DbCharacter character,
        bool paraLaVentana = false, bool onFoot = false, IReadOnlyList<long>? draftColors = null)
        => WriteLook(SemanticLook(character, paraLaVentana, onFoot, draftColors)).Build();

    public static EntityLook SemanticLook(DbCharacter character,
        bool paraLaVentana = false, bool onFoot = false, IReadOnlyList<long>? draftColors = null)
    {
        var overrides = new CharacterLookOverrides
        {
            OnFoot = onFoot,
            Colors = (draftColors != null && draftColors.Count > 0)
                ? CreationColors(character.Breed, character.Sex, draftColors)
                : null
        };
        return Build(character, paraLaVentana ? CharacterLookPurpose.AppearancePreview : CharacterLookPurpose.Normal, overrides);
    }

    /// <summary>A saved outfit's body and garments over the character's current equipment, on foot.</summary>
    public static byte[] OutfitLook(DbCharacter character, int headId, int bodyId,
        IReadOnlyList<long> colors, IReadOnlyList<Wardrobe.Worn> garments)
        => WriteLook(SemanticOutfitLook(character, headId, bodyId, colors, garments)).Build();

    internal static EntityLook SemanticOutfitLook(DbCharacter character, int headId, int bodyId,
        IReadOnlyList<long> colors, IReadOnlyList<Wardrobe.Worn> garments)
        => Build(character, CharacterLookPurpose.SavedOutfit, new()
        {
            HeadId = headId, BodyId = bodyId, Garments = garments,
            Colors = colors.Count > 0 ? colors : IndexColors(BreedLookCatalog.Get(character.Breed, character.Sex)?.Colors),
        });

    /// <summary>The selected body and head at the character's size, without equipment or attachments.</summary>
    public static byte[] BareLook(DbCharacter character)
        => WriteLook(Build(character, CharacterLookPurpose.Bare)).Build();

    /// <summary>
    /// A preset's supplied equipment, first alone and then covered by the current garments.
    /// Both are on foot without attachments; only the equipment wire format omits look type.
    /// </summary>
    public static (byte[] Equipment, byte[] Appearance) PresetLooks(DbCharacter character,
        IReadOnlyList<(int Slot, int Template)> worn)
    {
        var (equipment, appearance) = SemanticPresetLooks(character, worn);
        return (WriteLook(equipment, LookWireFormat.PresetEquipment).Build(), WriteLook(appearance).Build());
    }

    public static (EntityLook Equipment, EntityLook Appearance) SemanticPresetLooks(DbCharacter character,
        IReadOnlyList<(int Slot, int Template)> worn)
    {
        var overrides = new CharacterLookOverrides { Equipment = worn, Colors = SavedColors(character) };
        return (Build(character, CharacterLookPurpose.PresetEquipment, overrides) with { LookType = 0 },
            Build(character, CharacterLookPurpose.PresetAppearance, overrides));
    }

    private static ResolvedLook Resolve(DbCharacter character, CharacterLookPurpose purpose,
        CharacterLookOverrides overrides)
    {
        var policy = PolicyFor(purpose);
        if (ReplacementFor(character, purpose) is { } replacement) return new(replacement, null, []);

        // Preserve the legacy selected-session fallback for unowned normal/preview looks.
        long owner = policy.Mount && character.Id == 0 ? SessionContext.State.CharacterId : character.Id;
        var breed = BreedLookCatalog.Get(character.Breed, character.Sex);
        var colors = Ints(overrides.Colors ?? character.Colors.ToIndexed(breed?.Colors));
        var scales = breed != null ? Ints(CharacterSize.Applied(breed.Scales, owner)) : [];
        var equipment = policy.Equipment ? overrides.Equipment ?? Equipment.WornOf(owner) : [];
        var garments = policy.Garments ? overrides.Garments ?? Wardrobe.AppearanceOf(owner) : [];
        int bodyId = overrides.BodyId ?? (owner > 0 ? Wardrobe.BodyOf(owner) : 0);
        var body = BuildBodyLook(breed, BodyTable.SkinFor(bodyId, character.Breed, character.Sex),
            HeadTable.SkinFor(overrides.HeadId ?? character.HeadId, character.Breed, character.Sex),
            colors, scales, equipment, garments);

        // The look's f2 ("LookType", 3 in every capture) is the idle the character plays: the client
        // copies it into its look (vd::bqd) and EntityAnimator looks it up in IdlesDataRoot, 3 being
        // the plain one. In game it is the one the appearance window picked.
        if (purpose is CharacterLookPurpose.Normal or CharacterLookPurpose.Map
            && owner > 0 && Wardrobe.IdleOf(owner) is var idle && idle > 0)
            body = body with { LookType = idle };

        EntityLook? mount = null;
        if (policy.Mount && !overrides.OnFoot)
        {
            var ridden = character.Id != 0 ? Mounts.RiddenBy(character.Id) : Mounts.Ridden();
            var appearance = AppearanceMountFor(garments);
            // A player needs an actual ridden mount and usable bones. A cosmetic alone does not mount them.
            if (ridden != null && (appearance is { Bones: not 0 } ? appearance.Bones : ridden.Bones) != 0)
                mount = BuildMountLook(ridden, appearance);
        }

        int auraLevel = owner == character.Id ? character.Level : SessionContext.State.CharacterLevel;
        int auraBones = policy.Aura ? Cosmetics.AuraBones(Wardrobe.ShownBy(owner), auraLevel) : 0;
        bool includePets = policy.Pets && (mount == null || policy.MountedPets);
        return new(body, mount, AttachmentsFor(includePets ? garments : [], colors, auraBones));
    }

    private static EntityLook? ReplacementFor(DbCharacter character, CharacterLookPurpose purpose)
    {
        if (purpose == CharacterLookPurpose.Map && Phantoms.Is(character.Id))
            return MonsterMessages.Look(Phantoms.LookOf(character.Sex), false);
        if (purpose is not (CharacterLookPurpose.Normal or CharacterLookPurpose.Map)) return null;
        if (Mutations.OfCharacter(character.Id) is { } mutation)
            return MonsterMessages.Look(mutation.Look, false);
        if (purpose == CharacterLookPurpose.Map)
        {
            string? disguise = SlotLooks.DisguiseLook(SlotLooks.Of(character.Id));
            if (disguise != null) return MonsterMessages.Look(disguise, false);
        }
        return null;
    }

    /// <summary>All state is explicit here: no session, wardrobe, equipment or size queries.</summary>
    private static EntityLook BuildBodyLook(BreedLookCatalog.BreedLook? breed, int bodySkin, int headSkin,
        IReadOnlyList<int> colors, IReadOnlyList<int> scales,
        IReadOnlyList<(int Slot, int Template)> equipment, IReadOnlyList<Wardrobe.Worn> garments)
    {
        var skins = breed != null ? Ints(breed.Skins).ToList() : new List<int>();
        // The first skin selects the humanoid rig; the selected body must replace it in place.
        if (bodySkin > 0)
        {
            if (skins.Count > 0) skins[0] = bodySkin;
            else skins.Add(bodySkin);
        }
        if (headSkin > 0) skins.Add(headSkin);

        var covered = new HashSet<int>();
        foreach (var garment in garments)
        {
            // Even a hidden garment covers the real item underneath.
            switch (garment.Slot)
            {
                case Cosmetics.SlotHat: covered.Add(SlotHat); break;
                case Cosmetics.SlotCape: covered.Add(SlotCape); break;
                case Cosmetics.SlotShield: covered.Add(SlotShield); break;
            }
        }

        foreach (var (slot, template) in equipment)
        {
            if (covered.Contains(slot)) continue;
            int skin = EquipmentSkins.SkinOf(template);
            if (skin > 0 && !skins.Contains(skin)) skins.Add(skin);
        }
        foreach (var garment in garments)
        {
            if (garment.Hidden || garment.Slot is Cosmetics.SlotMount or Cosmetics.SlotPet) continue;
            foreach (int skin in Cosmetics.SkinsOf(garment.Gid, GarmentVariant(garment)))
                if (skin > 0 && !skins.Contains(skin)) skins.Add(skin);
        }
        return new(colors, LookType, breed?.Bones ?? 1, scales, skins, []);
    }

    /// <summary>Mounts own the root; pets and aura precede the rider and stay on that root.</summary>
    internal static EntityLook Compose(EntityLook body, EntityLook? mount,
        IReadOnlyList<SubEntityInformation> attachments)
    {
        if (mount == null)
            return attachments.Count == 0 ? body : body with { SubEntities = [.. body.SubEntities, .. attachments] };
        return mount with
        {
            SubEntities = [.. mount.SubEntities, .. attachments,
                new(body with { Bones = Mounts.RiderBones }, 0, SubEntityBindingPointCategory.HookPointCategoryMountDriver)],
        };
    }

    /// <summary>Already-resolved parts, used by server-dressed bot fighters.</summary>
    public static byte[] Composed(long bones, IReadOnlyList<long> skins, IReadOnlyList<long> colors,
        IReadOnlyList<long> scales, Mounts.Look? mount = null, Cosmetics.PieceLook? appearanceMount = null)
        => WriteLook(Compose(bones, skins, colors, scales, mount, appearanceMount)).Build();

    internal static EntityLook Compose(long bones, IReadOnlyList<long> skins, IReadOnlyList<long> colors,
        IReadOnlyList<long> scales, Mounts.Look? mount = null, Cosmetics.PieceLook? appearanceMount = null)
    {
        bool riding = mount != null || appearanceMount is { Bones: not 0 };
        var body = new EntityLook(Ints(colors), LookType, riding ? Mounts.RiderBones : checked((int)bones),
            Ints(scales), Ints(skins), []);
        var root = riding ? BuildMountLook(mount ?? new Mounts.Look
            { Bones = appearanceMount!.Bones, Scale = appearanceMount.Scale }, appearanceMount) : null;
        return Compose(body, root, []);
    }

    private static Cosmetics.PieceLook? AppearanceMountFor(IReadOnlyList<Wardrobe.Worn> garments)
    {
        Cosmetics.PieceLook? appearance = null;
        foreach (var garment in garments)
            if (garment.Slot == Cosmetics.SlotMount && !garment.Hidden && Cosmetics.MountLookOf(garment.Gid) is { } look)
                appearance = look;
        return appearance;
    }

    private static EntityLook BuildMountLook(Mounts.Look mount, Cosmetics.PieceLook? appearance)
    {
        int scale = appearance is { Scale: > 0 } ? appearance.Scale : mount.Scale;
        return new(appearance?.Colors != null ? PackedColors(appearance.Colors) : Ints(mount.Colors),
            LookType, appearance is { Bones: not 0 } ? appearance.Bones : mount.Bones,
            scale > 0 ? [scale] : [], appearance is { Skin: > 0 } ? [appearance.Skin] : [], []);
    }

    private static IReadOnlyList<SubEntityInformation> AttachmentsFor(IReadOnlyList<Wardrobe.Worn> garments,
        IReadOnlyList<int> wearerColors, int auraBones)
    {
        var attachments = new List<SubEntityInformation>();
        foreach (var garment in garments)
        {
            if (garment.Slot != Cosmetics.SlotPet || garment.Hidden || Cosmetics.PetOf(garment.Gid) is not { } pet) continue;
            var look = new EntityLook(pet.ColorsFromWearer ? wearerColors
                : pet.Colors != null ? PackedColors(pet.Colors) : [],
                LookType, pet.Bones, pet.Scale > 0 ? [pet.Scale] : [], [], []);
            attachments.Add(new(look, 0, SubEntityBindingPointCategory.HookPointCategoryPet));
        }
        if (auraBones > 0)
            attachments.Add(new(new([], LookType, auraBones, [], [], []), 0,
                SubEntityBindingPointCategory.HookPointCategoryBaseForeground));
        return attachments;
    }

    /// <summary>The portrait is the shared on-foot body, formatted for the sprite reader.</summary>
    public static string Drawable(DbCharacter character)
    {
        if (BreedLookCatalog.Get(character.Breed, character.Sex) == null) return "";
        var body = Build(character, CharacterLookPurpose.Portrait);
        if (body.Skins.Count == 0) return "";
        var colors = body.Colors.Select((color, index) => $"{index + 1}=#{color & 0xFFFFFF:X6}");
        int scale = body.Scales.Count > 0 ? body.Scales[0] : 100;
        return $"{{{body.Bones}|{string.Join(",", body.Skins)}|{string.Join(",", colors)}|{scale}}}";
    }

    /// <summary>Resolve creation RGB choices and -1 defaults without moving slots.</summary>
    public static List<long> CreationColors(int breedId, int sex, IReadOnlyList<long> colors)
        => CharacterColors.FromRgb(colors).ToIndexed(BreedLookCatalog.Get(breedId, sex)?.Colors);

    public static List<long> SavedColors(DbCharacter character)
        => character.Colors.ToIndexed(BreedLookCatalog.Get(character.Breed, character.Sex)?.Colors);

    public static List<long> IndexedColors(int breedId, int sex)
        => default(CharacterColors).ToIndexed(BreedLookCatalog.Get(breedId, sex)?.Colors);

    /// <summary>Outfit palettes carry plain RGB; entity looks carry slot indices in the high byte.</summary>
    public static List<long> PlainColors(int breedId, int sex, IReadOnlyList<long>? customColors)
    {
        var colors = customColors is { Count: > 0 } ? customColors : BreedLookCatalog.Get(breedId, sex)?.Colors;
        return colors?.Select(color => color & 0xFFFFFF).ToList() ?? [];
    }

    private static List<long> IndexColors(IReadOnlyList<long>? rgb)
        => rgb?.Select((color, index) => ((long)(index + 1) << 24) | (color & 0xFFFFFF)).ToList() ?? [];

    // AppearanceHandler encodes a living garment's variant as gid*1000+variant in its uid.
    private static int GarmentVariant(Wardrobe.Worn garment)
    {
        long variant = garment.Uid - garment.Gid * 1000L;
        return variant is >= 0 and < 1000 ? (int)variant : 0;
    }

    private static int[] Ints(IReadOnlyList<long> values)
        => values.Select(value => checked((int)value)).ToArray();

    // Catalogue palettes are stored as packed varints; this does not parse a built packet.
    private static int[] PackedColors(byte[] bytes)
    {
        var colors = new List<int>();
        using var input = new Google.Protobuf.CodedInputStream(bytes);
        while (!input.IsAtEnd) colors.Add(input.ReadInt32());
        return colors.ToArray();
    }

    /// <summary>Compatibility serializer for gameplay paths still consuming look bytes.</summary>
    internal static Pb WriteLook(EntityLook look) => WriteLook(look, LookWireFormat.Standard);

    private static Pb WriteLook(EntityLook look, LookWireFormat format)
    {
        var pb = Pb.New();
        if (look.Colors.Count > 0) pb.Packed(1, look.Colors.Select(value => (long)value).ToArray());
        if (format == LookWireFormat.PresetEquipment) pb.Var(3, look.Bones);
        else pb.Var(2, look.LookType).VarIfNotZero(3, look.Bones);
        if (look.Scales.Count > 0) pb.Packed(5, look.Scales.Select(value => (long)value).ToArray());
        if (look.Skins.Count > 0) pb.Packed(6, look.Skins.Select(value => (long)value).ToArray());
        foreach (var child in look.SubEntities)
        {
            int category = child.BindingPointCategory switch
            {
                SubEntityBindingPointCategory.HookPointCategoryPet => 1,
                SubEntityBindingPointCategory.HookPointCategoryMountDriver => 2,
                SubEntityBindingPointCategory.HookPointCategoryBaseForeground => 6,
                _ => throw new InvalidOperationException("Unsupported composed appearance attachment."),
            };
            pb.Msg(7, Pb.New().Msg(1, WriteLook(child.SubEntityLook))
                .VarIfNotZero(3, child.BindingPointIndex).Var(4, category));
        }
        return pb;
    }
}
