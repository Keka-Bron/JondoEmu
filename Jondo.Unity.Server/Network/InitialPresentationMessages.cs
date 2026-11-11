using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

/// <summary>Character-owned presentation and explicit server policies for initial selection.</summary>
internal sealed class InitialPresentationMessages(
    Func<CharacterCapabilitiesEvent> capabilities,
    Func<CharacterAppearanceCollectionEvent> colors,
    Func<ConfigurationEvent> configuration,
    Func<OutfitLookUpdatedEvent> look)
{
    // Guild creation/modification has no category-ownership restrictions in Jondo.
    // Advertise every category rather than the recorded account's 12287 mask.
    internal const int AllowedEmblemCategories = -1;
    // Jondo retains the current palette, without a saved-palette store or slot purchases.
    internal const int PaletteSlots = 1;
    // Explicit client-facing guild policy, alongside the limits used by the live engines.
    internal const int MaxExcludedRanks = 3;
    internal const int MaxExcludedCategories = 2;
    internal const long ActivityDelayMs = 24L * 60 * 60 * 1000;

    internal static InitialPresentationMessages Live(DatabaseManager.DbCharacter character) => new(
        () => new(AllowedEmblemCategories),
        () => new(new(PaletteSlots, [new(Palette(BreedLookTable.SavedColors(character)))], PaletteSlots)),
        () => new(GuildPolicy()),
        () => new(ConnectionProtocol.LookIdOf(character.Id), BreedLookTable.SemanticLook(character, paraLaVentana: true)));

    internal CharacterCapabilitiesEvent Capabilities() => capabilities();
    internal CharacterAppearanceCollectionEvent Colors()
    {
        var snapshot = colors();
        return new(snapshot.CharacterColors is { } value
            ? new(value.SlotsUnlocked, value.ColorPalettes.Select(palette => new ColorPalette(palette.Colors.ToArray())).ToArray(), value.SlotsTotal)
            : null);
    }
    internal ConfigurationEvent Configuration() => configuration();
    internal OutfitLookUpdatedEvent Look()
    {
        var snapshot = look();
        return new(snapshot.OutfitId, Snapshot(snapshot.Look));
    }

    private static EntityLook? Snapshot(EntityLook? value) => value == null ? null : new(
        value.Colors.ToArray(), value.LookType, value.Bones, value.Scales.ToArray(), value.Skins.ToArray(),
        value.SubEntities.Select(child => new SubEntityInformation(Snapshot(child.SubEntityLook)!,
            child.BindingPointIndex, child.BindingPointCategory)).ToArray());

    // Unused for outbound construction by project policy: the native audit was
    // indeterminate. Omit both tag-1 values, rather than replaying 206/135.
    // See docs/populated-unmapped-fields.md#after-selection-omission-policy.
    internal static OutfitAuraChangedEvent Aura() => new();
    internal static OrnamentSelectedEvent Ornament() => new();

    internal static GuildConfiguration GuildPolicy()
        // The unmapped outer configuration block and activity tag 7 are unused under
        // docs/populated-unmapped-fields.md#after-selection-omission-policy.
        => new(new(GuildMissionWatcher.TokenItem, GuildMissions.PerWeek, MaxExcludedRanks,
                MaxExcludedCategories, GuildActivity.AcknowledgmentItem, GuildActivity.AcknowledgmentCap),
            ActivityDelayMs, new(GuildStore.ContributionKamas, GuildActivity.ContributionTokens,
                GuildActivity.ContributionActivityPoints, GuildStore.ContributionGuildKamas));

    internal static IReadOnlyList<int> Palette(IEnumerable<long> indexedColors)
    {
        var result = new List<int>();
        foreach (long color in indexedColors)
        {
            if (color < 0) { result.Add(checked((int)color)); continue; }
            uint value = checked((uint)color);
            int slot = (int)(value >> 24);
            if (slot == 0) { result.Add((int)value); continue; }
            while (result.Count < slot) result.Add(-1);
            result[slot - 1] = (int)(value & 0xFFFFFF);
        }
        // Only RGB/inheritance values are supplied. Palette tag 1 and CharacterColors
        // tag 2 are unused: docs/populated-unmapped-fields.md#after-selection-omission-policy.
        return result.ToArray();
    }
}
