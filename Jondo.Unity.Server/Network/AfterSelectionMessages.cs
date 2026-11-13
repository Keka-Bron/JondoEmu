using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

/// <summary>Account/character snapshots used only by the after-selection manifest stage.</summary>
internal sealed class AfterSelectionMessages(
    Func<int, int> itemWeight,
    Func<IEnumerable<int>, IReadOnlyList<ItemSets.EquippedSet>> equippedSets,
    Func<long, long> storageKamas,
    ItemEffectMessages effects)
{
    internal static AfterSelectionMessages Live { get; } = new(DatabaseManager.GetItemRealWeight,
        ItemSets.Equipped, Bank.KamasOf, ItemEffectMessages.FromCatalogue);

    // Jondo has no persisted storage tags, marriage model or general advertised-bonus
    // feed. These explicit initial policies replace the recording's state. In particular,
    // opaque BonusType numbers are not Almanax bonus IDs, and are not translated as such.
    internal static TagStoragesRefreshEvent Tags() => new([]);
    internal static SpouseStatusEvent Spouse() => new(false);
    internal static BonusesEvent Bonuses() => new([]);

    internal IReadOnlyList<SetUpdateEvent> Sets(SessionState state)
    {
        var templates = state.EquipmentItems.Values.Where(item => Equipment.IsWorn(item.Position))
            .OrderBy(item => item.Position).ThenBy(item => item.Uid).Select(item => item.Template).ToArray();
        return equippedSets(templates).Select(set => new SetUpdateEvent(
            set.Effects.Select(effect => effects.Effect(new(effect.Effect, effect.Value, 0, 0)))
                .OfType<ObjectEffect>().ToArray(), set.Id,
            // The reviewed API calls this ObjectsUid. The native player-set model keeps
            // these beside the datacenter set's item templates; the capture also carries
            // template IDs, not the inventory's instance UIDs.
            set.Templates.ToArray())).ToArray();
    }

    internal InventoryWeightEvent Weight(SessionState state)
    {
        long carried = 0;
        foreach (var item in state.EquipmentItems.Values)
            carried = checked(carried + (long)itemWeight(item.Template) * Math.Max(1, item.Quantity));

        // The capacity every refresh sends (ConnectionProtocol.Capacity): levels, jobs, strength.
        // Arithmetic is widened before multiplication and checked at the int32 wire boundary.
        return new(checked((int)carried), checked((int)ConnectionProtocol.Capacity(state)));
    }

    internal StorageKamasUpdateEvent Storage(long accountId) => new(storageKamas(accountId));
}
