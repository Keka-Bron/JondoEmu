using Jondo.Unity.Protocol.Game;

namespace Jondo.Unity.Server.Network;

internal sealed class InventoryMessages(ItemEffectMessages effects)
{
    internal static InventoryMessages FromCatalogue { get; } = new(ItemEffectMessages.FromCatalogue);

    internal InventoryContentEvent Snapshot(SessionState state)
        => new(state.Kamas, state.EquipmentItems.Values.Select(item => new ObjectItemInventory(
            item.Position, false, new ObjectItem(item.Template,
                item.Effects.Select(effects.Effect).OfType<ObjectEffect>().ToArray(),
                Math.Max(1, item.Quantity), checked((int)item.Uid), []))).ToArray());

    internal Task SendAsync(GameSession session) => session.SendAsync(Snapshot(session.State));
}
