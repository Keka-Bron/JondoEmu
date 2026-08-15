using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Managers;
using Jondo.Unity.Launcher.Network;

namespace Jondo.Unity.Launcher.Handlers
{
    /// <summary>
    /// El cofre del merkasako.
    ///
    /// Leído de la captura del cofre de una casa, que usa el mismo protocolo:
    ///
    ///   cliente  iwo { f1: uid de habilidad, f2: elemento }    ha clicado el cofre
    ///   servidor iwn { f1:1, f2: elemento, f4: 104, f5: quién }
    ///   servidor kci { f1: 100, f3: 4 }                        el cofre se abre
    ///   servidor iwb { f1 (rep): lo que hay dentro }
    ///
    ///   cliente  kcr { f1: cuántos, f2: uid }                  mover un objeto
    ///   servidor iua / itd  el objeto que llega    itc / ium  el que se va    iun  el peso
    ///
    ///   cliente  kla        servidor khd { f3: 11 }            cerrar
    ///
    /// La dirección del movimiento no viaja en el kcr: se deduce de dónde esté el objeto ahora. Si
    /// lo tiene el inventario, entra al cofre; si lo tiene el cofre, sale a la bolsa. Es lo que hace
    /// el juego, y es lo único que puede ser: el cliente manda el mismo mensaje en los dos sentidos.
    ///
    /// El f1 es la cantidad, y llega como -1 cuando se arrastra la pila entera.
    /// </summary>
    public static class ChestHandler
    {
        /// <summary>El cofre que está abierto, para no atender un kcr con el cofre cerrado.</summary>
        private static bool _open;

        public static bool IsOpen => _open;

        /// <summary>¿El elemento que ha clicado es el cofre de este mapa?</summary>
        public static bool IsChest(long mapId, int elementId)
        {
            var chest = Merkasako.ChestOf(mapId);
            return chest.Id != 0 && chest.Id == elementId;
        }

        public static async Task OpenAsync(NetworkStream stream, int elementId)
        {
            _open = true;

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("iwn", ConnectionProtocol.BuildElementInUse(
                    elementId, Merkasako.ChestSkill, GameState.CharacterId)));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("kci", ConnectionProtocol.BuildStorageOpened()));

            var content = HavenBagStore.ChestOf(GameState.CharacterId);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("iwb", ConnectionProtocol.BuildStorageContent(content)));

            Console.WriteLine($"[Cofre] Abierto: {content.Count} objeto(s) dentro.");
        }

        public static async Task CloseAsync(NetworkStream stream)
        {
            _open = false;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("khd", ConnectionProtocol.BuildStorageClosed()));
        }

        public static async Task MoveAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? kcr = ConnectionProtocol.ReadPayload(payload, "kcr");
            if (kcr == null || !_open) return;

            long uid = 0;
            int quantity = 0;
            foreach (var field in ProtoMessage.Parse(kcr).Fields)
            {
                if (field.WireType != 0) continue;
                // El -1 viaja como el varint más grande que hay; significa "toda la pila".
                if (field.FieldNumber == 1)
                    quantity = field.VarIntValue < 0 || field.VarIntValue > int.MaxValue
                        ? 0 : (int)field.VarIntValue;
                else if (field.FieldNumber == 2) uid = field.VarIntValue;
            }
            if (uid == 0) return;

            long who = GameState.CharacterId;
            bool enElCofre = HavenBagStore.Holds(who, uid);

            if (enElCofre)
            {
                if (!HavenBagStore.TakeOut(who, uid, quantity)) return;
                await AnnounceAsync(stream, uid, ArrivesInBag, "itc", "sale del cofre");
            }
            else
            {
                if (!HavenBagStore.PutIn(who, uid, quantity)) return;
                await AnnounceAsync(stream, uid, ArrivesInChest, "ium", "entra en el cofre");
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("iun",
                    ConnectionProtocol.BuildPods(0, 1000 + 5L * GameState.StatStrength)));
        }

        /// <summary>
        /// Los cuatro mensajes del trasiego, y cuál es cuál.
        ///
        /// Se ven emparejados en la captura del cofre de una casa: van en grupos de tres, y los dos
        /// grupos son <c>iua, itc, iun</c> y <c>itd, ium, iun</c>. Cada grupo es UN movimiento, así
        /// que el que llega y el que se va de cada grupo son los dos extremos del mismo viaje:
        ///
        ///   itc  se va del cofre        iua  llega a la bolsa      (sacar)
        ///   ium  se va de la bolsa      itd  llega al cofre        (meter)
        ///
        /// Los tenía cruzados, y por eso el objeto desaparecía del sitio del que salía pero no
        /// aparecía en el que entraba hasta cerrar y volver a abrir. Y por eso el premio de la
        /// lotería tampoco se veía llegar al inventario.
        ///
        /// El que llega va con todo —plantilla, efectos, cantidad—; el que se va, solo con su
        /// identificador.
        /// </summary>
        public const string ArrivesInBag = "iua";
        public const string ArrivesInChest = "itd";

        /// <summary>El campo donde va el objeto en cada uno: f3 en el iua, f1 en el itd.</summary>
        public static int FieldOf(string opcode) => opcode == ArrivesInBag ? 3 : 1;

        private static async Task AnnounceAsync(NetworkStream stream, long uid, string llega,
                                                string seVa, string que)
        {
            var destino = BuscarDondeEsta(uid);
            if (destino != null)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(llega,
                        ConnectionProtocol.BuildItemArrived(FieldOf(llega), destino)));
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(seVa, ConnectionProtocol.BuildItemGone(uid)));

            Console.WriteLine($"[Cofre] El objeto {uid} {que}.");
        }

        private static HavenBagStore.StoredItem? BuscarDondeEsta(long uid)
        {
            foreach (var item in HavenBagStore.ChestOf(GameState.CharacterId))
            {
                if (item.Uid == uid) return item;
            }
            return HavenBagStore.FromInventory(GameState.CharacterId, uid);
        }
    }
}
