using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal sealed class InventoryMessages(ItemEffectMessages effects)
{
    internal static InventoryMessages FromCatalogue { get; } = new(ItemEffectMessages.FromCatalogue);

    internal InventoryContentEvent Snapshot(SessionState state)
        // The invisible items (type 203) are in the bag but not in the list: the client would show them.
        => new(state.Kamas, state.EquipmentItems.Values.Where(item => !InvisibleItems.Conceal(item.Template, item.Uid))
            .Select(item => new ObjectItemInventory(
            item.Position, false, new ObjectItem(item.Template,
                item.Effects.Select(effects.Effect).OfType<ObjectEffect>().ToArray(),
                Math.Max(1, item.Quantity), checked((int)item.Uid), []))).ToArray());

    internal Task SendAsync(GameSession session) => session.SendAsync(Snapshot(session.State));
}
