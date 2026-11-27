using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;
using Game = Jondo.Unity.Protocol.Game;

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
                Console.WriteLine($"[Teleport] Unknown route: map {sourceMapId}, element {elementId}.");
                return;
            }

            // A door of the abyssal base lets through only while its lights are lit.
            if (!Managers.AbyssalBase.MayPass(sourceMapId, elementId))
            {
                await Managers.AbyssalBase.TellShutAsync(stream);
                Console.WriteLine($"[Teleport] Element {elementId} on {sourceMapId}: its door is shut.");
                await RefuseAsync(stream, elementId, skillId, route.SourceCellId, route.InteractiveType);
                return;
            }

            // Nor a plate gate that is itself the passage, while it stands shut: the Kralamoure's.
            if (!Managers.PlateGates.MayPass(sourceMapId, elementId))
            {
                await Managers.PlateGates.TellShutAsync(stream);
                Console.WriteLine($"[Teleport] Element {elementId} on {sourceMapId}: its gate is shut.");
                await RefuseAsync(stream, elementId, skillId, route.SourceCellId, route.InteractiveType,
                                   Managers.PlateGates.IsGateElement(sourceMapId, elementId)
                                       ? Managers.PlateGates.StateOf(sourceMapId) : 0);
                return;
            }

            // A passage with a criterion opens only for whoever meets it: a vilinsekt's hideout to
            // the rikiki wand worn. Refused, the client hears why, as for an item's criterion.
            // And the element is released all the same: left without an answer to its iwo, the
            // client never sends another for it until the map is loaded again -- the death mask
            // put on after a refusal, Ilyzaelle's portal stayed deaf.
            if (route.Criterion.Length > 0 && !Managers.ItemCriteria.Holds(route.Criterion))
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Lqn, route.Refusal != 0
                        ? ConnectionProtocol.BuildInfoMessage(route.RefusalType, route.Refusal)
                        : ConnectionProtocol.BuildInfoMessage(
                            Managers.InfoMessages.Warning, Managers.ItemCriteria.RefusalOf(route.Criterion))));
                Console.WriteLine($"[Teleport] Element {elementId} on {sourceMapId}: «{route.Criterion}» not met.");
                await RefuseAsync(stream, elementId, skillId, route.SourceCellId, route.InteractiveType);
                return;
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iwn, ConnectionProtocol.BuildElementInUse(
                    elementId, skillId, SessionContext.State.CharacterId)));
            // A gate's element is put back as it stands -- open --, not as an element at rest.
            await ReleaseAsync(stream, elementId, skillId, route.SourceCellId, route.InteractiveType,
                               Managers.PlateGates.IsGateElement(sourceMapId, elementId)
                                   ? Managers.PlateGates.StateOf(sourceMapId) : 0);

            // The same door to somewhere else while a quest says so: the smoke-filled bank.
            long toMap = route.DestinationMapId;
            int toCell = route.DestinationCellId;
            if (route.InsteadMapId != 0 && route.InsteadWhen.Length > 0 && Managers.ItemCriteria.Holds(route.InsteadWhen))
            {
                toMap = route.InsteadMapId;
                toCell = route.InsteadCellId;
            }

            int landed = await ToMapAsync(stream, toMap, toCell);
            if (landed >= 0)
                Console.WriteLine($"[Teleport] Element {elementId}: {sourceMapId} -> " +
                                  $"{toMap}, cell {landed}.");
        }

        /// <summary>
        /// A passage refused: the use is answered as taken and released at once (iwn, then
        /// ReleaseAsync), with no map change. The client keeps the element it asked for until it
        /// sees it used; left without that, it never asks for that element again until the map
        /// is loaded anew.
        /// </summary>
        internal static async Task RefuseAsync(NetworkStream stream, int elementId, int skillId, int cellId, int type,
                                               int state = 0)
        {
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iwn, ConnectionProtocol.BuildElementInUse(
                    elementId, skillId, SessionContext.State.CharacterId)));
            await ReleaseAsync(stream, elementId, skillId, cellId, type, state);
        }

        /// <summary>
        /// A passage's use closed before the map changes: the use ended (iwi), the element available
        /// again (iwf) and declared anew (iwm), so its graphic is still there on coming back.
        /// </summary>
        internal static async Task ReleaseAsync(NetworkStream stream, int elementId, int skillId, int cellId, int type,
                                                int state = 0)
        {
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iwi,
                    ConnectionProtocol.BuildInteractiveUseEnded(elementId, skillId)));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iwf,
                    ConnectionProtocol.BuildElementState(cellId, elementId, state: state)));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iwm,
                    ConnectionProtocol.BuildElementRedeclared(
                        Interactives.SkillInstanceOf(elementId), skillId,
                        elementId, type, usable: true)));
        }

        /// <summary>
        /// Leaves the character on that map and tells the client. Returns the cell he landed on, or -1 if
        /// the map is no good.
        /// </summary>
        public static Task<int> ToMapAsync(NetworkStream stream, long mapId, int targetCell = MapCentre)
            => ToMapAsync(SessionContext.Current, mapId, targetCell);

        public static Task<int> ToMapAsync(GameSession session, long mapId, int targetCell = MapCentre)
            => ToMapAsync(session, mapId, targetCell, admitted: null);

        internal static async Task<int> ToMapAsync(GameSession session, long mapId, int targetCell,
            IReadOnlyList<Jondo.Unity.ServerMods.ICompanion>? admitted)
        {
            if (!await CanTeleportAsync(session, mapId)) return -1;
            return await MoveToMapAsync(session, mapId, targetCell, admitted);
        }

        internal static async Task<bool> CanTeleportAsync(GameSession session, long mapId)
        {
            if (mapId <= 0) return false;

            // A prisoner goes nowhere until his time is up: see Managers.Jail. Every road that
            // ends in a teleport ends here, so this is the one that cannot be forgotten.
            var refusal = Jail.HoldNotice(session) ?? GuildRaidManager.ClosedFloorNotice(session.CharacterId, mapId, session.State.Language);
            if (refusal != null)
            {
                await session.SendAsync(new Game.TextInformationEvent(Game.TextInformationType.TextInformationMessage, InfoMessages.FreeText, [refusal]));
                return false;
            }

            // A map that is not in the world data is a map the client cannot load either: it gets
            // the jru, finds nothing and the character appears nowhere. It is the same check the
            // zaap and the map change by the edge do.
            if (MapManager.GetMapInfo(mapId) == null)
            {
                Console.WriteLine($"[Teleport] Map {mapId} is not in the world data. Not going.");
                return false;
            }
            return true;
        }

        internal static async Task<int> MoveToMapAsync(GameSession session, long mapId, int targetCell,
            IReadOnlyList<Jondo.Unity.ServerMods.ICompanion>? admitted = null)
        {
            var previousMap = session.Map;
            session.State.MapId = mapId;
            session.State.CellId = MapManager.GetNearestWalkableCell(mapId, targetCell);
            DatabaseManager.SaveCharacter(session.State);

            // The other two maps involved: the one he leaves and the one he enters. Without this,
            // teleporting left a ghost where he was and he arrived invisible where he went.
            await SessionRegistry.AnunciarMudanzaAsync(session, previousMap, admitted: admitted);

            // The traveler's orientation resets to zero; peer removal belongs to the map transition above.
            await session.SendAsync(new Game.MapChangeOrientationEvent(session.CharacterId, Game.Direction.East));
            await session.SendAsync(new Game.MapCurrentEvent(mapId));
            await session.SendAsync(ServerTimeMessages.Snapshot());
            await EntryEnvironmentMessages.SendDiscoveredAsync(session, mapId);

            Console.WriteLine($"[Teleport] To map {mapId}, cell {session.State.CellId}. " +
                              "Waiting for the jrh.");
            return session.State.CellId;
        }
    }
}
