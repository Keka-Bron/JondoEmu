using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Using an item from the bag: a double click.
    ///
    ///   client  iuu { f3: uid }
    ///
    /// It was answered by nothing, whatever the item. What is handled here is the box, the item
    /// whose effect 222 says "¿Qué hay ahí dentro?" (<see cref="LootBoxes"/>): one box is spent
    /// and what it gives goes into the bag, each with its chat line. The answer has the shape of
    /// the captures' item uses ("utilizar pocima de recuerdo"): the spent stack's new total
    /// (ivj) -- or its leaving (ium) when it was the last --, then the bag's weight (iun); and
    /// each item that arrives, as the gathering and the shops hand them over, with "Has
    /// conseguido {0} '$item{1}'" (info 21).
    ///
    /// Other uses -- potions, scrolls, books -- are still not handled, and are logged.
    /// </summary>
    public static class ItemUseHandler
    {
        public static async Task UseAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? iuu = ConnectionProtocol.ReadPayload(payload, Op.Iuu);
            if (iuu == null) return;

            long uid = 0;
            foreach (var field in ProtoMessage.Parse(iuu).Fields)
                if (field.FieldNumber == 3 && field.WireType == 0) uid = field.VarIntValue;
            if (uid == 0) return;

            var item = Equipment.ByUid(uid);
            if (item == null || item.Quantity <= 0) return;

            if (LootBoxes.BoxOf(item.Template) is not { } box)
            {
                Console.WriteLine($"[Items] Using item {item.Template} (uid {uid}) is not handled yet.");
                return;
            }

            long character = SessionContext.State.CharacterId;
            int left = item.Quantity - 1;
            if (!DatabaseManager.DestroyCharacterItem(character, uid, 1)) return;
            Equipment.Remove(uid, 1);
            await WriteAsync(stream, left > 0
                ? ConnectionProtocol.Push(Op.Ivj, ConnectionProtocol.BuildItemQuantity(uid, left))
                : ConnectionProtocol.Push(Op.Ium, ConnectionProtocol.BuildItemGone(uid)));

            var given = LootBoxes.Open(box.Group, box.Draws, Random.Shared);
            foreach (var (gid, quantity) in given)
            {
                if (!await WorkshopHandler.GiveAsync(stream, gid, quantity)) continue;
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildSystemMessage(InfoMessages.ItemGained, quantity.ToString(), gid.ToString())));
            }

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Iun,
                ConnectionProtocol.BuildPods(0, 1000 + 5L * SessionContext.State.TotalStrength)));

            Console.WriteLine($"[Items] {character} opens a {item.Template} (group {box.Group.Id}): " +
                              string.Join(", ", given.ConvertAll(g => $"{g.Quantity} x {g.ItemId}")) + ".");
        }

        private static Task WriteAsync(NetworkStream stream, byte[] frame)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);
    }
}
