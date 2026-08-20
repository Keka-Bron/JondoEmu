using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Managers;
using Jondo.Unity.Launcher.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Launcher.Handlers
{
    /// <summary>
    /// Abrir una papelera.
    ///
    /// Medido de la captura de la que hay delante del banco de Bonta, y es el mismo baile que el
    /// cofre del merkasako:
    ///
    ///   cliente  iwo { f1: habilidad, f2: elemento }   ha clicado la papelera
    ///   servidor iwn                                   el elemento está en uso
    ///   servidor kci                                   se abre el almacén
    ///   servidor iwb { lista de objetos }              lo que hay dentro
    ///
    /// ─── Abren vacías, y es a propósito ─────────────────────────────────────────────────────
    ///
    /// Una papelera guarda lo que OTROS han tirado. En este servidor nadie ha tirado nada, así que
    /// no hay nada que enseñar. Llenarlas de objetos inventados metería en el mundo cosas que no
    /// vienen de ninguna parte, que es justo lo que no se hace aquí.
    ///
    /// ─── Lo que todavía no hace ─────────────────────────────────────────────────────────────
    ///
    /// Meter y sacar objetos NO está conectado. El cofre lo hace con
    /// <c>SessionContext.State.IsChestOpen</c>, pero ese interruptor manda los objetos al cofre del
    /// merkasako del personaje: usarlo aquí haría que lo que se tire a una papelera aparezca en tu
    /// casa. Antes de conectarlo hace falta un almacén propio por papelera, y eso es otra tarea.
    /// Mientras tanto abre, se ve vacía y se cierra, que es lo que se pidió.
    /// </summary>
    public static class BinHandler
    {
        private static readonly List<Managers.HavenBagStore.StoredItem> Empty = new();

        public static async Task OpenAsync(NetworkStream stream, int elementId, int skillId)
        {
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iwn, ConnectionProtocol.BuildElementInUse(
                    elementId, skillId, SessionContext.State.CharacterId)));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Kci, ConnectionProtocol.BuildStorageOpened()));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iwb, ConnectionProtocol.BuildStorageContent(Empty)));

            Console.WriteLine($"[Papeleras] Abierta la del mapa {SessionContext.State.MapId}, vacía.");
        }
    }
}
