using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// El cofre del merkasako.
    /// </summary>
    /// <remarks>
    /// Measured on "Interactivos varios/abrir cofre de mi merkasako-cambiar cosas entre cofre e
    /// inventario-cerrar.pcapng", frame numbers being positions in <c>hilo.tramas</c>:
    ///
    ///   4   C iwo { f1: 4493210, f2: 516924 }        the chest
    ///   5   S iwn
    ///   6   S kci { f1: 2147483647, f3: 19 }          the haven bag's window: kind 19
    ///   7   S iwb { what is inside }
    ///   12  C kcr { f1: -1, f2: 530092084 }  → itc, iua (534451715, a NEW uid), kcu, iun
    ///   18  C kcr { f1: 1,  f2: 530090564 }  → ium, itd (534456574, a new uid), kcu, iun
    ///   33  C kla → 34 S khd { f3: 11 }
    ///
    /// It is the storage every other one is, with two differences the capture shows: kci's kind is
    /// 19 and not the house chest's 4 -- which is what this sent until now -- and what leaves goes
    /// out BEFORE what arrives, where the house chest, the bin and the guild chest send the
    /// arrival first.
    ///
    /// Two things this used to get wrong. kcr's f1 is a signed count, and -1 is ONE unit out, not
    /// "the whole stack": the bin in front of the Bonta bank takes a stack of four one unit per -1
    /// (see <see cref="StorageHandler"/>). And a stack changes uid when it changes side, as every
    /// capture of a storage shows; keeping the same uid on both sides put two stacks with one uid
    /// in the client the moment a move was partial.
    ///
    /// Left out: kcu { f1, f3 }, the chest's weight and what it can hold (1878 of 11578 there).
    /// The chest's capacity is not known.
    /// </remarks>
    public static class ChestHandler
    {
        /// <summary>El cofre que está abierto, para no atender un kcr con el cofre cerrado.</summary>
        public static bool IsOpen => SessionContext.State.IsChestOpen;

        /// <summary>¿El elemento que ha clicado es el cofre de este mapa?</summary>
        public static bool IsChest(long mapId, int elementId)
        {
            var chest = Merkasako.ChestOf(mapId);
            return chest.Id != 0 && chest.Id == elementId;
        }

        public static Task OpenAsync(NetworkStream stream, int elementId)
            => OpenAsync(stream, elementId, Merkasako.ChestSkill);

        public static async Task OpenAsync(NetworkStream? stream, int elementId, int skillId)
        {
            SessionContext.State.IsChestOpen = true;

            await StorageHandler.SendAsync(stream, Op.Iwn, ConnectionProtocol.BuildElementInUse(
                elementId, skillId, SessionContext.State.CharacterId));

            await StorageHandler.SendAsync(stream, Op.Kci, StorageProtocol.BuildHavenBagOpened());

            var content = HavenBagStore.ChestOf(SessionContext.State.CharacterId);
            await StorageHandler.SendAsync(stream, Op.Iwb, ConnectionProtocol.BuildStorageContent(content));

            Console.WriteLine($"[Chest] Opened: {content.Count} stack(s) inside.");
        }

        public static async Task CloseAsync(NetworkStream? stream)
        {
            SessionContext.State.IsChestOpen = false;
            await StorageHandler.SendAsync(stream, Op.Khd, ConnectionProtocol.BuildStorageClosed());
        }

        public static async Task MoveAsync(NetworkStream? stream, byte[] payload)
        {
            if (!SessionContext.State.IsChestOpen) return;
            if (!StorageHandler.ReadMove(payload, out int quantity, out long uid)) return;

            await StorageHandler.MoveAsync(stream, StorageStacks.HavenBag(SessionContext.State.CharacterId),
                                           quantity, uid, goneFirst: true);
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
        public const string ArrivesInBag = Op.Iua;
        public const string ArrivesInChest = Op.Itd;

        /// <summary>El campo donde va el objeto en cada uno: f3 en el iua, f1 en el itd.</summary>
        public static int FieldOf(string opcode) => opcode == ArrivesInBag ? 3 : 1;
    }
}
