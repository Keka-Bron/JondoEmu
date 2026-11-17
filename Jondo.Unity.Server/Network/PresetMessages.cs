using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal interface IPresetMessagesBackend
{
    PresetLook Looks(DbCharacter character, IReadOnlyList<(int Position, Equipment.Item Item)> placed);
    IReadOnlyDictionary<int, long> Bonuses(IReadOnlyList<(int Position, Equipment.Item Item)> placed);
    long BaseValue(int id, int level, GameSession owner);
    bool HasOutfit(long characterId, string uuid);
}

/// <summary>Detached saved builds, using the owner's bag and characteristic columns.</summary>
internal sealed class PresetMessages(IPresetMessagesBackend backend, ItemEffectMessages effects)
{
    internal static PresetMessages Live { get; } = new(new LiveBackend(), ItemEffectMessages.FromCatalogue);

    private static readonly int[] SheetIds =
    [
        0, 1, 10, 11, 12, 141, 13, 142, 14, 15, 16, 18, 19, 23, 25, 26, 27, 28, 33, 34, 35, 36, 37,
        40, 44, 48, 49, 54, 55, 56, 57, 58, 75, 78, 79, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92,
        120, 121, 122, 123, 124, 125,
    ];

    internal PresetListEvent Snapshot(GameSession owner, DbCharacter character, IEnumerable<CharacterPresets.Preset> presets)
    {
        using var context = SessionContext.Push(owner);
        var result = new Dictionary<string, CharacterPresetInfo>();
        foreach (var preset in presets)
        {
            var placed = new List<(int Position, Equipment.Item Item)>();
            foreach (var (position, uid) in preset.Items)
                if (Equipment.IsWorn(position) && owner.State.EquipmentItems.TryGetValue(uid, out var item))
                    placed.Add((position, item));

            var equipment = placed.ToDictionary(row => row.Position, row => new PresetEquipment(row.Item.Template,
                checked((int)row.Item.Uid), row.Item.Effects.Select(effects.Effect).OfType<ObjectEffect>().ToArray()));
            var spells = new SpellPreset(preset.Spells.ToDictionary(row => row.Key, row => new SpellPositions(row.Value.ToArray())));
            // Created is the only stored date. Retain its exact text at the reviewed
            // last-modification field; do not invent a new timestamp or storage policy.
            result[preset.Uuid] = new(backend.Looks(character, placed), preset.Icon,
                Characteristics(owner, character.Level, preset.Points, backend.Bonuses(placed)), preset.Name, equipment,
                preset.Created, spells, false,
                preset.Outfit.Length > 0 && backend.HasOutfit(character.Id, preset.Outfit) ? preset.Outfit : "", preset.Uuid);
        }
        // There is no forgettable-preset source in this sender. Its type-only values
        // are not synthesized.
        return new(result, new Dictionary<string, ForgettableSpellPresetInfo>());
    }

    internal PresetCharacteristicsInfo Characteristics(GameSession owner, int level,
        IReadOnlyDictionary<int, int> points, IReadOnlyDictionary<int, long> equipment)
    {
        var state = owner.State;
        var scrolls = new Dictionary<int, long>
        {
            [Stat.Strength] = state.ScrolledStrength, [Stat.Vitality] = state.ScrolledVitality,
            [Stat.Wisdom] = state.ScrolledWisdom, [Stat.Chance] = state.ScrolledChance,
            [Stat.Agility] = state.ScrolledAgility, [Stat.Intelligence] = state.ScrolledIntelligence,
        };
        long Total(int id) => checked(scrolls[id] + points.GetValueOrDefault(id) + equipment.GetValueOrDefault(id));

        return new(SheetIds.ToDictionary(id => id, id => scrolls.TryGetValue(id, out long scrolled)
            ? new PresetCharacteristicsDetails(checked((int)scrolled), points.GetValueOrDefault(id), 0, 0, null,
                checked((int)equipment.GetValueOrDefault(id)))
            : new PresetCharacteristicsDetails(0, 0, checked((int)CharacteristicRules.GivenByCharacteristics(id, Total)),
                checked((int)backend.BaseValue(id, level, owner)), null, checked((int)equipment.GetValueOrDefault(id)))));
    }

    private sealed class LiveBackend : IPresetMessagesBackend
    {
        public PresetLook Looks(DbCharacter character, IReadOnlyList<(int Position, Equipment.Item Item)> placed)
        {
            var (equipment, appearance) = CharacterLookBuilder.SemanticPresetLooks(character,
                placed.Select(row => (row.Position, row.Item.Template)).ToArray());
            return new(equipment, appearance);
        }
        public IReadOnlyDictionary<int, long> Bonuses(IReadOnlyList<(int Position, Equipment.Item Item)> placed)
            => Equipment.BonusesOf(placed);
        public long BaseValue(int id, int level, GameSession owner) => CharacteristicsMessages.Live.ValueOf(id, level, owner);
        public bool HasOutfit(long characterId, string uuid) => Outfits.Of(characterId).Exists(outfit => outfit.Uuid == uuid);
    }
}
