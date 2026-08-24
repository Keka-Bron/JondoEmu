using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Managers;
using Jondo.Unity.Launcher.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Launcher.Handlers
{
    /// <summary>
    /// Llevar al personaje a un mapa cualquiera sin que haya andado hasta él.
    ///
    /// No es un camino nuevo: es el mismo que ya hacen el zaap
    /// (<see cref="ZaapTravelHandler.TravelAsync"/>) y el cambio de mapa por el borde
    /// (<see cref="WorldMoveHandler.ChangeMapAsync"/>), y son los cuatro mensajes que manda la
    /// captura real, en este orden:
    ///
    ///   jsd   el personaje sale del mapa en el que estaba — antes de decirle que cargue otro
    ///   jru   carga este mapa
    ///   lqu   el reloj del servidor, que viaja con el jru en todos los cambios de la captura
    ///   hjk   mapa descubierto
    ///
    /// Y ahí se para: el cliente contesta con un jrh y es GameNodeProxy quien le manda entonces el
    /// bloque del mapa con todo lo que hay dentro. Por eso esto no manda ningún jss.
    /// </summary>
    public static class TeleportHandler
    {
        /// <summary>
        /// A dónde se apunta cuando no hay una casilla concreta a la que llegar.
        ///
        /// Es el centro aproximado del tablero —el mismo 280 con el que nace un personaje en
        /// GameState— y no se usa tal cual: <see cref="MapManager.GetNearestWalkableCell"/> busca
        /// desde ahí la andable más cercana. Aterrizar en el centro y no en la esquina importa
        /// porque desde el borde el cliente puede pedir salida de mapa nada más llegar.
        /// </summary>
        public const int MapCentre = 280;

        /// <summary>
        /// Usar un paso registrado en el mapa donde está el personaje.
        ///
        /// Los tres mensajes de en medio no son adorno. El «.sun» de Giny cuelga la acción del
        /// elemento sin quitarle su gráfico, y en el cliente 3.6 dejar sólo el iwn antes de
        /// cambiar de mapa deja a veces el elemento marcado como ocupado en su caché: al volver,
        /// su gráfico ya no está. Cerrar el uso, devolverlo a disponible y volver a declararlo
        /// hace que el ciclo se pueda repetir.
        /// </summary>
        public static async Task UseAsync(NetworkStream stream, int elementId, int skillId)
        {
            long sourceMapId = SessionContext.State.MapId;
            if (!TeleportManager.TryGet(sourceMapId, elementId, out var route))
            {
                Console.WriteLine($"[Teleport] Ruta desconocida: mapa {sourceMapId}, elemento {elementId}.");
                return;
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iwn, ConnectionProtocol.BuildElementInUse(
                    elementId, skillId, SessionContext.State.CharacterId)));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iwi,
                    ConnectionProtocol.BuildInteractiveUseEnded(elementId, skillId)));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iwf,
                    ConnectionProtocol.BuildElementState(
                        route.SourceCellId, elementId, state: 0)));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iwm,
                    ConnectionProtocol.BuildElementRedeclared(
                        Interactives.SkillInstanceOf(elementId), skillId,
                        elementId, route.InteractiveType, usable: true)));

            int landed = await ToMapAsync(stream, route.DestinationMapId, route.DestinationCellId);
            if (landed >= 0)
                Console.WriteLine($"[Teleport] Elemento {elementId}: {sourceMapId} -> " +
                                  $"{route.DestinationMapId}, casilla {landed}.");
        }

        /// <summary>
        /// Deja al personaje en ese mapa y avisa al cliente. Devuelve la casilla donde ha caído,
        /// o -1 si el mapa no sirve.
        /// </summary>
        public static async Task<int> ToMapAsync(NetworkStream stream, long mapId,
                                                 int targetCell = MapCentre)
        {
            if (mapId <= 0) return -1;

            // Un mapa que no está en los datos del mundo es un mapa que el cliente tampoco sabe
            // cargar: recibe el jru, no encuentra nada y el personaje no aparece en ningún sitio.
            // Es la misma comprobación que hacen el zaap y el cambio de mapa por el borde.
            if (MapManager.GetMapInfo(mapId) == null)
            {
                Console.WriteLine($"[Teleport] El mapa {mapId} no está en los datos del mundo. No se va.");
                return -1;
            }

            long mapaQueDeja = SessionContext.State.MapId;
            SessionContext.State.MapId = mapId;
            SessionContext.State.CellId = MapManager.GetNearestWalkableCell(mapId, targetCell);
            DatabaseManager.SaveCurrentCharacter();

            // Los otros dos mapas implicados: del que sale y al que entra. Sin esto, teletransportarse
            // dejaba un fantasma donde estaba y llegaba invisible a donde iba.
            await SessionRegistry.AnunciarMudanzaAsync(SessionContext.Current, mapaQueDeja);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildActorLeft(SessionContext.State.CharacterId));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildLoadMap(mapId));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildMapClock());
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildMapDiscovered(mapId));

            Console.WriteLine($"[Teleport] Al mapa {mapId}, casilla {SessionContext.State.CellId}. " +
                              "Esperando el jrh.");
            return SessionContext.State.CellId;
        }
    }
}
