using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal static class TaxCollectorMessages
{
    internal static TaxCollectorMovement Movement(TaxCollectorStore.Absence report, int x, int y)
        => new(report.WhoId, report.LastNameId, new(report.MapId, x, report.SubAreaId, y),
            report.FirstNameId, report.WhoName ?? "", (TaxCollectorMovementType)report.Kind);

    internal static TaxCollectorInformation Information(TaxCollectorStore.TaxCollector collector,
        AllianceStore.Alliance alliance, TaxCollectorState state, int x, int y, int pods, long worth,
        IEnumerable<HavenBagStore.StoredItem> equipment, IReadOnlyDictionary<int, int> spells,
        IEnumerable<(int Id, long Base, long Bonus)> sheet, Func<int, int> itemType, ItemEffectMessages effects)
        => new(collector.OwnerId,
            pods == 0 && worth == 0 ? [] : [new(new TaxCollectorComplementaryInformationCaseLootInformation(new(pods, worth)))],
            new(x, y), collector.FirstNameId,
            equipment.Select(item => new ObjectItemInventory(TaxCollectorProtocol.SlotOfType.GetValueOrDefault(itemType(item.Gid)),
                false, new(item.Gid, Equipment.ParseEffects(item.Effects).Select(effects.Effect).OfType<ObjectEffect>().ToArray(),
                    Math.Max(1, item.Quantity), checked((int)item.Uid), []))).ToArray(), collector.OwnerName ?? "",
            spells.Select(spell => new TaxCollectorOrderedSpell(spell.Value, spell.Key)).ToArray(),
            collector.PlacedUtcS, state, collector.LastNameId, collector.SubAreaId, collector.Uuid,
            MonsterMessages.Look(TaxCollectorProtocol.LookString(alliance, TaxCollectorProtocol.MapBones), false),
            MapActorMessages.Alliance(alliance), collector.MapId, sheet.Select(Characteristic).ToArray());

    private static CharacterCharacteristic Characteristic((int Id, long Base, long Bonus) value)
        => new(value.Id, CharacteristicCatalogue.Default.KindOf(value.Id) switch
        {
            CharacteristicKind.Value => new CharacterCharacteristicCaseValue(new(checked(value.Base + value.Bonus))),
            CharacteristicKind.Usable => new CharacterCharacteristicCaseUsable(new(checked((int)value.Base), 0,
                checked((int)value.Bonus), 0, 0, 0, 0)),
            _ => new CharacterCharacteristicCaseDetailed(new(checked((int)value.Base), 0, 0, 0, checked((int)value.Bonus), 0)),
        });
}
