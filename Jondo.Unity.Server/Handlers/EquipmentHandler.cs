using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Launcher.Handlers
{
    /// <summary>
    /// Moving an item between the bag and a slot.
    ///
    ///   C  iuk { f1: how many, f2: item uid, f3: where it goes }
    ///   S  ivq { f1: item uid, f2: where it went }
    ///   S  lym { f1: 206 }        the same 206 in every capture
    ///   S  hie { f1: 2 }          likewise
    ///   S  hii { f1: 2 }          likewise
    ///   S  iun                    pods, because what is worn still weighs
    ///
    /// Positions come from the captures and from a session of the real client: 0 the amulet,
    /// 2 to 5 the rings and the belt, 6 the hat, 8 the pet or the mount, 12 to 14 the dofus, and
    /// 63 the bag, which is where an item goes when it is taken off.
    ///
    /// En cada hueco cabe una cosa, y eso lo hace cumplir este handler: lo que ya estuviera puesto
    /// sale a la bolsa con su propio ivq antes de que entre lo nuevo.
    ///
    /// The database is authoritative for both inventory ownership and characteristics.  An item
    /// the client did not receive from that inventory is never moved or persisted: it is a stale
    /// capture/UI item, not something the character owns.
    /// </summary>
    public static class EquipmentHandler
    {
        /// <summary>Where an item goes when it is taken off.</summary>
        public const int Bag = 63;

        public static async Task MoveAsync(NetworkStream stream, byte[] payload, long accountId = 0)
        {
            byte[]? iuk = ConnectionProtocol.ReadPayload(payload, Op.ObjectSetPositionMessage);
            if (iuk == null || iuk.Length == 0) return;

            // Position zero, not the bag. The amulet's slot IS zero, so proto3 leaves the field
            // out and the message arrives with nothing but the uid — which is exactly what the
            // client sends when you try to put an amulet on. Defaulting to the bag answered
            // "it went back in the bag" every time, and the amulet was the one piece of equipment
            // that could never be put on.
            long uid = 0;
            int position = 0;
            foreach (var f in ProtoMessage.Parse(iuk).Fields)
            {
                if (f.WireType != 0) continue;
                if (f.FieldNumber == 2) uid = f.VarIntValue;
                else if (f.FieldNumber == 3) position = (int)f.VarIntValue;
            }
            if (uid == 0) return;

            // Never acknowledge a move for an item that is not owned by the selected character.
            // Older world-entry captures can leave the client with the recorder's inventory; an
            // optimistic ivq for one of those UIDs makes it look equipped while there is no item
            // (and therefore no effects or mount) in the server model.  Re-send the complete
            // authoritative inventory instead, so the client discards that stale projection.
            if (Managers.Equipment.ByUid(uid) == null)
            {
                long characterId = SessionContext.State.CharacterId;
                if (characterId > 0) Managers.Equipment.LoadFrom(characterId);

                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.InventoryContentMessage, ConnectionProtocol.BuildInventory()));
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.InventoryWeightMessage, ConnectionProtocol.BuildPods(0, Capacity())));
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.CharacterStatsListMessage, ConnectionProtocol.BuildCharacteristics()));

                Console.WriteLine($"[Equipment] Rejected unknown item {uid}; authoritative inventory resent.");
                return;
            }

            // En un hueco cabe uno. Lo que hubiera puesto sale a la bolsa antes de que entre lo
            // nuevo, y se le manda su propio ivq: sin eso las dos cosas se quedaban en el mismo
            // hueco a la vez y el aspecto lo decidía la primera que se encontrase, no la que el
            // jugador acababa de ponerse.
            foreach (var evicted in Managers.Equipment.Occupants(position, uid))
            {
                evicted.Position = Bag;
                DatabaseManager.SaveItemPosition(evicted.Uid, Bag);

                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.ObjectMovementMessage, Pb.New().Var(1, evicted.Uid).Var(2, Bag).Build()));

                Console.WriteLine($"[Equipment] El hueco {position} lo ocupaba {evicted.Uid}; " +
                                  "a la bolsa.");
            }

            DatabaseManager.SaveItemPosition(uid, position);
            Managers.Equipment.Move(uid, position);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.ObjectMovementMessage, Pb.New().Var(1, uid).Var(2, position).Build()));

            // The three of unknown meaning that travel with it, each with the value it carries in
            // every equip and unequip capture there is.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lym, Pb.New().Var(1, 206).Build()));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Hie, Pb.New().Var(1, 2).Build()));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Hii, Pb.New().Var(1, 2).Build()));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.InventoryWeightMessage,
                    ConnectionProtocol.BuildPods(0, Capacity())));

            // Y el aspecto, que es lo que hace que el personaje se suba a la montura sin tener que
            // recargar el mapa. Son dos mensajes y hacen falta los dos: el jsn redibuja al muñeco
            // del mapa y el lxc actualiza el de la ficha. En la captura salen en este orden, entre
            // los tres de arriba y el peso.
            var character = DatabaseManager.GetCharacterById(Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId);
            if (character != null)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.GameContextRefreshEntityLookMessage, ConnectionProtocol.BuildActorRefreshed(
                        character, Jondo.Unity.Launcher.Network.SessionContext.State.CellId, Jondo.Unity.Launcher.Network.SessionContext.State.Orientation, accountId)));

                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.AppearancePreviewLookMessage, ConnectionProtocol.BuildLookChanged(character)));
            }

            // And the sheet, because what the item gives goes with it. Without this the numbers
            // only caught up on the next entry into the world.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.CharacterStatsListMessage, ConnectionProtocol.BuildCharacteristics()));

            Console.WriteLine($"[Equipment] Item {uid} -> position {position}"
                              + (position == Bag ? " (taken off)." : "."));
        }

        private static long Capacity()
        {
            var bonuses = Managers.Equipment.Bonuses();
            bonuses.TryGetValue(ConnectionProtocol.Stat.Strength, out long equippedStrength);
            return 1000 + 5L * Math.Max(0,
                Jondo.Unity.Launcher.Network.SessionContext.State.StatStrength + equippedStrength);
        }
    }
}
