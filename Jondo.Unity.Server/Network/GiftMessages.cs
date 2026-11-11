using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal static class GiftMessages
{
    internal static GameActionItemListEvent Snapshot(IEnumerable<Gifts.Gift> gifts)
        => new(gifts.Select(gift => new GameActionItem(
            // Descriptions remain in storage; unmapped content tag 6 is unused by
            // the inspected gift UI. See docs/populated-unmapped-fields.md.
            new GameActionItemContent(gift.Title, [new(gift.Quantity, gift.Gid, [])]),
            [gift.Id])).ToArray());
}
