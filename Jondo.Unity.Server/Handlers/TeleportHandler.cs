using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Taking the character to any map without him having walked there.
    ///
    /// It is not a new road: it is the same the zaap (<see cref="ZaapTravelHandler.TravelAsync"/>) and
    /// the map change by the edge (<see cref="WorldMoveHandler.ChangeMapAsync"/>) already take, and
    /// they are the four messages the real capture sends, in this order:
    ///
    ///   jsd   the character leaves the map he was on -- before telling him to load another
    ///   jru   load this map
    ///   lqu   the server clock, which travels with the jru in all the capture's changes
    ///   hjk   map discovered
    ///
    /// And it stops there: the client answers with a jrh and it is GameNodeProxy that then sends it the
    /// map block with everything inside. That is why this sends no jss.
    /// </summary>
    public static class TeleportHandler
    {
        /// <summary>
        /// Where to aim when there is no specific cell to land on.
        ///
        /// It is the approximate centre of the board -- the same 280 a character is born with in GameState
        /// -- and it is not used as is: <see cref="MapManager.GetNearestWalkableCell"/> looks from there
        /// for the nearest walkable one. Landing in the centre and not in a corner matters because from
        /// the edge the client can ask for a map exit right on arriving.
        /// </summary>
        public const int MapCentre = 280;

        /// <summary>
        /// Using a passage registered on the map the character is on.
        ///
        /// The three messages in the middle are not decoration. Giny's «.sun» hangs the action off the
        /// element without removing its graphic, and in the 3.6 client leaving only the iwn before changing
        /// map sometimes leaves the element marked as busy in its cache: on coming back, its graphic is
        /// gone. Closing the use, putting it back to available and declaring it again makes the cycle
        /// repeatable.
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
        /// Leaves the character on that map and tells the client. Returns the cell he landed on, or -1 if
        /// the map is no good.
        /// </summary>
        public static async Task<int> ToMapAsync(NetworkStream stream, long mapId,
                                                 int targetCell = MapCentre)
        {
            if (mapId <= 0) return -1;

            // A prisoner goes nowhere until his time is up: see Managers.Jail. Every road that
            // ends in a teleport ends here, so this is the one that cannot be forgotten.
            if (await Managers.Jail.KeepsInAsync(stream)) return -1;

            // A map that is not in the world data is a map the client cannot load either: it gets
            // the jru, finds nothing and the character appears nowhere. It is the same check the
            // zaap and the map change by the edge do.
            if (MapManager.GetMapInfo(mapId) == null)
            {
                Console.WriteLine($"[Teleport] El mapa {mapId} no está en los datos del mundo. No se va.");
                return -1;
            }

            long mapaQueDeja = SessionContext.State.MapId;
            SessionContext.State.MapId = mapId;
            SessionContext.State.CellId = MapManager.GetNearestWalkableCell(mapId, targetCell);
            DatabaseManager.SaveCurrentCharacter();

            // The other two maps involved: the one he leaves and the one he enters. Without this,
            // teleporting left a ghost where he was and he arrived invisible where he went.
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
