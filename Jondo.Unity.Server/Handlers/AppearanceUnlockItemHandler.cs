using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The items that unlock a face, a body or a pose for the appearance window
    /// (<see cref="AppearanceUnlocks"/>): used, the account keeps what they carry and the item is
    /// spent; lyb { f1: the ids, f3: the item, f4: their kind } puts the ids in the client's list,
    /// so the tab offers them. An item whose every id is already the account's stays.
    /// INFERRED from the client (eqq::bbhc): no capture uses one.
    /// </summary>
    public static class AppearanceUnlockItemHandler
    {
        /// <summary>An unlocking item used: false when the item is no such thing.</summary>
        public static async Task<bool> TryUseAsync(NetworkStream stream, Equipment.Item item)
        {
            var unlocks = AppearanceUnlocks.Of(
                DatabaseManager.GetItemEffectsData(DatabaseManager.GetItemTemplatePossibleEffects(item.Template)));
            if (unlocks is not { } unlock) return false;
            if (!await ItemUseHandler.CriterionHoldsAsync(stream, item)) return true;

            long account = SessionContext.Current.AccountId;
            var added = AppearanceUnlocks.Add(account, unlock.Kind, unlock.Ids);
            if (added.Count == 0)
            {
                Console.WriteLine($"[Appearances] {SessionContext.State.CharacterName} already has what {item.Template} unlocks.");
                return true;
            }

            var entry = new CosmeticCollection.Entry
            {
                Uid = item.Uid, Gid = item.Template, Quantity = 1, Effects = Forgemagic.Serialize(item.Effects),
            };
            if (!await ItemUseHandler.SpendOneAsync(SessionContext.Current, item.Uid)) return true;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push("lyb", Pb.New()
                .Packed(1, added.ConvertAll(id => (long)id))
                .Msg(3, CosmeticCollectionHandler.Body(entry))
                .Var(4, (int)unlock.Kind)
                .Build()));

            Console.WriteLine($"[Appearances] {SessionContext.State.CharacterName} unlocks {unlock.Kind} " +
                              $"{string.Join(", ", added)} with {item.Template}.");
            return true;
        }
    }
}
