using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal sealed class CosmeticCollectionMessages(ItemEffectMessages effects)
{
    internal static CosmeticCollectionMessages Live { get; } = new(ItemEffectMessages.FromCatalogue);

    internal CosmeticInventoryLoadedEvent Snapshot(IEnumerable<int> heads, IEnumerable<int> idles,
        IEnumerable<int> bodies, IEnumerable<CosmeticCollection.Entry> entries)
        => new(heads.ToArray(), idles.ToArray(), bodies.ToArray(), entries.Select(entry => new ObjectItem(entry.Gid,
            Equipment.ParseEffects(entry.Effects).Select(effects.Effect).OfType<ObjectEffect>().ToArray(),
            Math.Max(1, entry.Quantity), checked((int)entry.Uid), [])).ToArray());

    internal CosmeticInventoryLoadedEvent ForAccount(long accountId) => Snapshot(
        AppearanceUnlocks.All(accountId, AppearanceUnlocks.Kind.Head),
        AppearanceUnlocks.All(accountId, AppearanceUnlocks.Kind.Idle),
        AppearanceUnlocks.All(accountId, AppearanceUnlocks.Kind.Body), CosmeticCollection.Of(accountId));
}
