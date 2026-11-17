using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using StoredOutfit = Jondo.Unity.Server.Managers.Outfits.Outfit;

namespace Jondo.Unity.Server.Network;

internal interface IWardrobeMessagesBackend
{
    Outfits.Look Worn(DbCharacter character);
    IReadOnlyList<long> Colors(DbCharacter character, Outfits.Look look);
    EntityLook Look(DbCharacter character, Outfits.Look look, bool kept);
}

/// <summary>Detached outfits from live or saved appearance state, never from serialized packets.</summary>
internal sealed class WardrobeMessages(IWardrobeMessagesBackend backend)
{
    internal static WardrobeMessages Live { get; } = new(new LiveBackend());

    internal OutfitsLoadedEvent Snapshot(GameSession owner, DbCharacter character, IEnumerable<StoredOutfit> outfits)
    {
        using var context = SessionContext.Push(owner);
        var entries = outfits.Select(outfit => Outfit(character, outfit)).ToArray();
        return new(entries, entries.FirstOrDefault());
    }

    internal Outfit Outfit(DbCharacter character, StoredOutfit outfit, string? uuid = null)
    {
        var look = outfit.Kept ?? backend.Worn(character);
        var colors = backend.Colors(character, look).Select(value => checked((int)value)).ToArray();
        // Tag 11 is the reviewed head ID, not the server's outfit sorting number.
        // See docs/populated-unmapped-fields.md#wardrobe-construction-policy.
        return new(colors.Length > 0 ? new(colors) : null, false,
            outfit.Created.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            look.Body > 0 ? look.Body : BodyTable.BaseOf(character.Breed, character.Sex), outfit.Favorite,
            uuid ?? outfit.Uuid, look.Idle > 0 ? look.Idle : IdleTable.Plain,
            Optional(look.Aura), Optional(look.Title), look.Head > 0 ? look.Head : character.HeadId,
            Copy(backend.Look(character, look, outfit.Kept != null)), outfit.Name,
            look.Garments.Where(worn => worn.Hidden).Select(worn => (ObjectSlot)worn.Slot).ToArray(),
            outfit.Icon, Optional(look.Ornament), look.Garments.Where(worn => worn.Gid != 0)
                .ToDictionary(worn => worn.Slot, worn => new SkinOutfit(null, worn.Gid)));
    }

    internal static TitlesAndOrnamentsEvent Owned(IEnumerable<long> titles, IEnumerable<long> ornaments)
        => new(titles.Select(id => checked((int)id)).ToArray(), ornaments.Select(id => checked((int)id)).ToArray());

    internal static OutfitLookUpdatedEvent LookChanged(DbCharacter character)
        => new(ConnectionProtocol.LookIdOf(character.Id), CharacterLookBuilder.SemanticLook(character, paraLaVentana: true));

    private static int? Optional(int value) => value == 0 ? null : value;

    private static EntityLook Copy(EntityLook look) => new(look.Colors.ToArray(), look.LookType, look.Bones,
        look.Scales.ToArray(), look.Skins.ToArray(), look.SubEntities.Select(child =>
            new SubEntityInformation(Copy(child.SubEntityLook), child.BindingPointIndex, child.BindingPointCategory)).ToArray());

    /// <summary>Adapter for the still-legacy outfit window/action responses.</summary>
    internal static byte[] Write(Outfit outfit)
    {
        var body = Pb.New();
        if (outfit.Colors != null) body.Msg(1, Pb.New().Packed(2, outfit.Colors.Colors.Select(value => (long)value)));
        body.VarIfNotZero(2, outfit.HarnessColorsEnabled ? 1 : 0).Str(3, outfit.LastModified)
            .VarIfNotZero(5, outfit.BodyId).VarIfNotZero(6, outfit.Favorite ? 1 : 0).Str(7, outfit.Uuid)
            .VarIfNotZero(8, outfit.IdleId);
        if (outfit.AuraEmoteId is { } aura) body.Var(9, aura);
        if (outfit.TitleId is { } title) body.Var(10, title);
        body.VarIfNotZero(11, outfit.HeadId);
        if (outfit.EntityLook != null) body.Msg(12, CharacterLookBuilder.WriteLook(outfit.EntityLook));
        if (outfit.Name.Length > 0) body.Str(13, outfit.Name);
        if (outfit.HiddenSlots.Count > 0) body.Packed(14, outfit.HiddenSlots.Select(slot => (long)slot));
        body.Var(15, outfit.PictogramId);
        if (outfit.OrnamentId is { } ornament) body.Var(16, ornament);
        foreach (var (slot, skin) in outfit.Objects)
        {
            var value = Pb.New().VarIfNotZero(2, skin.Gid);
            if (skin.Skin is { } id) value.Var(1, id);
            body.Msg(17, Pb.New().VarIfNotZero(1, slot).Msg(2, value));
        }
        return body.Build();
    }

    private sealed class LiveBackend : IWardrobeMessagesBackend
    {
        public Outfits.Look Worn(DbCharacter character) => Outfits.Worn(character);
        public IReadOnlyList<long> Colors(DbCharacter character, Outfits.Look look)
            => CharacterLookBuilder.PlainColors(character.Breed, character.Sex, look.Colors);
        public EntityLook Look(DbCharacter character, Outfits.Look look, bool kept)
            => kept ? CharacterLookBuilder.SemanticOutfitLook(character, look.Head > 0 ? look.Head : character.HeadId,
                look.Body, look.Colors, look.Garments) : CharacterLookBuilder.SemanticLook(character, paraLaVentana: true);
    }
}
