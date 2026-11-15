using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server;

/// <summary>Selects the character state and attachments a presentation needs.</summary>
public enum CharacterLookPurpose
{
    Normal,
    Map,
    AppearancePreview,
    Bare,
    SavedOutfit,
    PresetEquipment,
    PresetAppearance,
    Portrait,
}

/// <summary>
/// Optional inputs for a look. Null inherits current character state; an empty collection
/// explicitly supplies none. Colors are indexed look colors, not plain storage RGB.
/// The purpose determines which equipment, garments and attachments may be included.
/// </summary>
public sealed record CharacterLookOverrides
{
    public int? HeadId { get; init; }
    public int? BodyId { get; init; }
    public IReadOnlyList<long>? Colors { get; init; }
    public IReadOnlyList<(int Slot, int Template)>? Equipment { get; init; }
    public IReadOnlyList<Wardrobe.Worn>? Garments { get; init; }
    public bool OnFoot { get; init; }
}
