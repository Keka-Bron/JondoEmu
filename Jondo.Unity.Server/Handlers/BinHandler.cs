using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// A bin: opening it, and with it open, the moves of any storage (see
    /// <see cref="StorageHandler"/>).
    /// </summary>
    /// <remarks>
    /// Measured on "Interactivos varios/abrir papelera frente a banco bonta y sacar cosas.pcapng",
    /// frame numbers being positions in <c>hilo.tramas</c>:
    ///
    ///   10  C iwo { f1: 30733, f2: 523673 }        the bin
    ///   11  S iwn { f1: 1, f2: 523673, f4: 153, f5: character }
    ///   12  S kci { f1: 100, f3: 17 }               the bin's window: kind 17, not the chest's 4
    ///   13  S iwb { eight stacks }                  what others threw in
    ///   21  C kcr { f1: -1, f2: 537199889 } → iua (a new uid), itc, iun
    ///   31  C kcr { f1: -1, f2: 537192145 } → iua, itd (3 left), iun; then ivj 2, itd 2; ivj 3,
    ///       itd 1; ivj 4, itc -- one unit per -1
    ///   83  C kla → 84 S khd { f3: 11 }
    ///
    /// A bin is PUBLIC: what one player throws in, the next one to open it finds, and it stays
    /// through restarts. Each bin element is its own storage. Throwing in is not captured; it is
    /// the chest's itd, ium, iun, the frames every storage sends for it.
    /// </remarks>
    public static class BinHandler
    {
        public static async Task OpenAsync(NetworkStream? stream, int elementId, int skillId)
        {
            await StorageHandler.SendAsync(stream, Op.Iwn, ConnectionProtocol.BuildElementInUse(
                elementId, skillId, SessionContext.State.CharacterId));

            long mapId = SessionContext.State.MapId;
            var window = new StorageHandler.Window
            {
                Kind = StorageHandler.Kind.Bin,
                Place = StorageStacks.Bin(mapId, elementId),
                MapId = mapId,
                ElementId = elementId,
            };
            await StorageHandler.OpenAsync(stream, window, StorageProtocol.BuildBinOpened());

            Console.WriteLine($"[Bins] Opened the one of map {mapId}, element {elementId}: " +
                              $"{StorageStacks.ItemsOf(window.Place).Count} stack(s) inside.");
        }
    }
}
