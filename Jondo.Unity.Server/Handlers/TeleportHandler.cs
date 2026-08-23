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

        /// <summary>Exécute un passage interactif enregistré sur la map courante.</summary>
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

            int landed = await ToMapAsync(stream, route.DestinationMapId, route.DestinationCellId);
            if (landed >= 0)
                Console.WriteLine($"[Teleport] Elemento {elementId}: {sourceMapId} -> " +
                                  $"{route.DestinationMapId}, casilla {landed}.");
        }

        /// <summary>
        /// Exécute la même route générique lorsque le personnage termine son déplacement sur la
        /// cellule de l'élément puis envoie jqi. Giny appelle alors le même UseInteractive que le
        /// clic; Jondo résout ici cette route par (MapId, SourceCellId).
        /// </summary>
        public static async Task<bool> TryUseCellTriggerAsync(NetworkStream stream)
        {
            long sourceMapId = SessionContext.State.MapId;
            int sourceCellId = SessionContext.State.CellId;
            if (!TeleportManager.TryGetCellTrigger(sourceMapId, sourceCellId, out var route))
                return false;

            int landed = await ToMapAsync(stream, route.DestinationMapId, route.DestinationCellId);
            if (landed < 0) return false;

            Console.WriteLine($"[Teleport] Fin de mouvement/JQI {sourceMapId}/{sourceCellId} -> " +
                              $"{route.DestinationMapId}, casilla {landed}.");
            return true;
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
