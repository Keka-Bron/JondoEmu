using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Destroying an inventory item: right click, destroy, accept.
    ///
    ///   client  iuw { f2 { f2: uid, f3: how many } }
    ///
    /// The client removes nothing by itself: it sends the request and waits. Without an answer, the
    /// item stays where it is, which is what used to happen -- the iuw had been showing up in the log
    /// of unhandled messages for a while.
    ///
    /// The answer is the same as when an item leaves the bag for any other reason:
    ///
    ///   ium { f1: uid }   it goes          or   iua, if only the quantity drops
    ///   iun               the weight, which is now lower
    /// </summary>
    public static class DestroyItemHandler
    {
        public static async Task DestroyAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? iuw = ConnectionProtocol.ReadPayload(payload, Op.Iuw);
            if (iuw == null) return;

            long uid = 0;
            int quantity = 0;

            // Va envuelto: f2 { f2: uid, f3: cuántos }.
            foreach (var field in ProtoMessage.Parse(iuw).Fields)
            {
                if (field.FieldNumber != 2 || field.WireType != 2) continue;
                foreach (var inner in ProtoMessage.Parse(field.BytesValue).Fields)
                {
                    if (inner.WireType != 0) continue;
                    if (inner.FieldNumber == 2) uid = inner.VarIntValue;
                    else if (inner.FieldNumber == 3) quantity = (int)inner.VarIntValue;
                }
            }
            if (uid == 0) return;

            var item = Equipment.ByUid(uid);
            if (item == null)
            {
                Console.WriteLine($"[Inventario] Piden destruir {uid}, que no es nuestro.");
                return;
            }

            int destruye = quantity <= 0 || quantity >= item.Quantity ? item.Quantity : quantity;
            bool entero = destruye >= item.Quantity;

            if (!DatabaseManager.DestroyCharacterItem(Jondo.Unity.Server.Network.SessionContext.State.CharacterId, uid, destruye)) return;
            Equipment.Remove(uid, destruye);

            if (entero)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Ium, ConnectionProtocol.BuildItemGone(uid)));
            }
            else
            {
                // There is still some: it is sent again with the new quantity.
                var queda = HavenBagStore.FromInventory(Jondo.Unity.Server.Network.SessionContext.State.CharacterId, uid);
                if (queda != null)
                {
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(ChestHandler.ArrivesInBag,
                            ConnectionProtocol.BuildItemArrived(
                                ChestHandler.FieldOf(ChestHandler.ArrivesInBag), queda)));
                }
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iun,
                    ConnectionProtocol.BuildPods(0, 1000 + 5L * Jondo.Unity.Server.Network.SessionContext.State.TotalStrength)));

            Console.WriteLine($"[Inventario] Destruido {destruye} de {uid}" +
                              (entero ? " (entero)." : "."));
        }
    }
}
