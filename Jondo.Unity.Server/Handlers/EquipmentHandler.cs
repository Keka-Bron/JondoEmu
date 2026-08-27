using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
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
    /// One thing this does NOT do yet is change the characteristics. Equipment adds its bonus in
    /// field 7 of each entry of kub, and filling that in means knowing which item the uid is,
    /// which means the inventory coming out of the database instead of out of the capture. Until
    /// then the item moves and the sheet does not follow.
    /// </summary>
    public static class EquipmentHandler
    {
        /// <summary>Where an item goes when it is taken off.</summary>
        public const int Bag = 63;

        public static async Task MoveAsync(NetworkStream stream, byte[] payload, long accountId = 0)
        {
            byte[]? iuk = ConnectionProtocol.ReadPayload(payload, Op.Iuk);
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

            // En un hueco cabe uno. Lo que hubiera puesto sale a la bolsa antes de que entre lo
            // nuevo, y se le manda su propio ivq: sin eso las dos cosas se quedaban en el mismo
            // hueco a la vez y el aspecto lo decidía la primera que se encontrase, no la que el
            // jugador acababa de ponerse.
            var evictedUids = new System.Collections.Generic.List<long>();
            foreach (var evicted in Managers.Equipment.Occupants(position, uid))
            {
                evictedUids.Add(evicted.Uid);
                evicted.Position = Bag;
                DatabaseManager.SaveItemPosition(evicted.Uid, Bag, SessionContext.State.CharacterId);

                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Ivq, Pb.New().Var(1, evicted.Uid).Var(2, Bag).Build()));

                Console.WriteLine($"[Equipment] El hueco {position} lo ocupaba {evicted.Uid}; " +
                                  "a la bolsa.");
            }

            // The item may well not be ours: the inventory the client is showing is still the one
            // replayed from the capture, and those uids are not in our database. The move is
            // answered either way, which is what the real server does, and it is written down when
            // it is an item we actually hold.
            DatabaseManager.SaveItemPosition(uid, position, SessionContext.State.CharacterId);
            bool known = Managers.Equipment.Move(uid, position);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ivq, Pb.New().Var(1, uid).Var(2, position).Build()));

            // The three of unknown meaning that travel with it, each with the value it carries in
            // every equip and unequip capture there is.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lym, Pb.New().Var(1, 206).Build()));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Hie, Pb.New().Var(1, 2).Build()));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Hii, Pb.New().Var(1, 2).Build()));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iun,
                    ConnectionProtocol.BuildPods(0, 1000 + 5L * Jondo.Unity.Server.Network.SessionContext.State.StatStrength)));

            // Y el aspecto, que es lo que hace que el personaje se suba a la montura sin tener que
            // recargar el mapa. Son dos mensajes y hacen falta los dos: el jsn redibuja al muñeco
            // del mapa y el lxc actualiza el de la ficha. En la captura salen en este orden, entre
            // los tres de arriba y el peso.
            var character = DatabaseManager.GetCharacterById(Jondo.Unity.Server.Network.SessionContext.State.CharacterId);
            if (character != null)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Jsn, ConnectionProtocol.BuildActorRefreshed(
                        character, Jondo.Unity.Server.Network.SessionContext.State.CellId, Jondo.Unity.Server.Network.SessionContext.State.Orientation, accountId)));

                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Lxc, ConnectionProtocol.BuildLookChanged(character)));
            }

            // And the sheet, because what the item gives goes with it. Without this the numbers
            // only caught up on the next entry into the world.
            if (known)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Kub, ConnectionProtocol.BuildCharacteristics()));
            }

            Console.WriteLine($"[Equipment] Item {uid} -> position {position}"
                              + (position == Bag ? " (taken off)." : ".")
                              + (known ? "" : " Not one of ours; the sheet is left alone."));
            ActivityJournal.Current.Write("equipment.moved",
                accountId > 0 ? accountId : SessionContext.Current.AccountId,
                SessionContext.State.CharacterId,
                new { uid, position, known, evicted = evictedUids });
        }
    }
}
