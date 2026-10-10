using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Using a zaap.
    ///
    /// Read from three real captures -- opening the list, travelling from Animatopia to the Castle of
    /// Amakna, and Bonta's zaapi -- and it is two steps:
    ///
    ///   client   iwo { f1: skill uid, f2: element }                clicked the zaap
    ///   server   iwn { f1: 1, f2: uid, f4: skill, f5: who }        the element is in use
    ///   server   hjj { f2: map where it is, f3 (repeated): a destination }   the list
    ///
    ///   client   hjc { f3: destination map }                       has chosen
    ///   server   jru + the whole map + ivf with the kamas left
    ///
    /// Each destination of the hjj is:
    ///
    ///   f1: the zone's level        f2: what going costs
    ///   f5: the map                 f6: the subarea
    ///
    /// f6 was checked against MapPositions in the capture's twenty-five entries and fits all of them.
    /// The destination one is already at travels without f2, which is proto3 saying it costs zero.
    /// </summary>
    /// <remarks>
    /// It is called ZaapTravelHandler and not ZaapHandler because that name is already taken: the
    /// launcher's ZaapHandler lives in Network, and it has nothing to do with this -- it is the Thrift
    /// service that talks to the client before entering the game.
    /// </remarks>
    public static class ZaapTravelHandler
    {
        /// <summary>
        /// What travelling costs, in kamas.
        ///
        /// The real server works it out by distance -- in the capture they go from 170 to 1080 and the far
        /// destinations are the expensive ones -- but the exact formula is in no client data. This one is
        /// ours, and does the same: the farther, the more it costs, with a floor and a ceiling within the
        /// range seen in the capture.
        /// </summary>
        private const int MinimumCost = 10;
        private const int MaximumCost = 1000;
        private const int CostPerStep = 10;

        /// <summary>The map of the zaap open right now, to charge from the right place.</summary>
        /// <summary>
        /// The client has clicked the zaap. He is answered that the element is in use and sent the list of
        /// destinations.
        /// </summary>
        public static async Task OpenAsync(NetworkStream stream, Interactives.Element zaap, int skillId)
        {
            long here = Jondo.Unity.Server.Network.SessionContext.State.MapId;

            SessionContext.State.OpenZaapMapId = here;

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iwn, ConnectionProtocol.BuildElementInUse(
                    zaap.Id, skillId, Jondo.Unity.Server.Network.SessionContext.State.CharacterId)));

            // From a VESTIGE one does not go to the world's zaaps: only to anomalies. Measured -- the
            // hjj answering the Cuna de Alma vestige carries two entries and both are anomalies,
            // while a normal zaap's carries forty-eight --. It makes sense: a vestige is not a zaap,
            // it is where an anomaly surfaces.
            bool vestige = Interactives.IsVestige(here, zaap);
            var destinations = vestige ? AnomalyDestinations(here) : Destinations(here);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Hjj, ConnectionProtocol.BuildZaapList(here, destinations)));

            Console.WriteLine($"[{(vestige ? "Vestige" : "Zaap")}] Opened on map {here}: " +
                              $"{destinations.Count} destinations.");
        }

        /// <summary>
        /// The close button. The client sends an empty kla and waits: the window does not close until the
        /// server answers.
        /// </summary>
        public static async Task CloseAsync(NetworkStream stream)
        {
            SessionContext.State.OpenZaapMapId = 0;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Kld, ConnectionProtocol.BuildDialogClosed()));
        }

        /// <summary>The client has chosen a destination. He is charged and taken.</summary>
        public static async Task TravelAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? hjc = ConnectionProtocol.ReadPayload(payload, Op.Hjc);
            if (hjc == null) return;
            // Before any kamas are paid for the trip.
            if (await Managers.Jail.KeepsInAsync(stream)) return;

            // f2 says which tab the choice comes from, and what f3 means depends on it: for the
            // zaap and the zaapi it is the MAP being gone to; for the anomaly it is ITS SUBAREA,
            // which is what identifies it. Measured: hjc { f2: 4, f3: 609 } answered with a jru to
            // 196085762, which is no map of subarea 609.
            long chosen = 0;
            int kind = 0;
            foreach (var field in ProtoMessage.Parse(hjc).Fields)
            {
                if (field.WireType != 0) continue;
                if (field.FieldNumber == 2) kind = (int)field.VarIntValue;
                else if (field.FieldNumber == 3) chosen = field.VarIntValue;
            }
            if (chosen <= 0) return;

            long from = SessionContext.State.OpenZaapMapId != 0
                ? SessionContext.State.OpenZaapMapId
                : Jondo.Unity.Server.Network.SessionContext.State.MapId;

            long target;
            long cost;
            string what;

            if (kind == Anomalies.Kind)
            {
                if (!Anomalies.TryGet((int)chosen, out var anomaly))
                {
                    Console.WriteLine($"[Anomalies] The client asks for subarea {chosen} and it is not " +
                                      "in the list. No travelling.");
                    return;
                }

                // It is charged by where the vestige is, not by where one ends up: in the capture the
                // six anomaly costs are identical to those of the normal zaap to that same map.
                target = Anomalies.ArrivalMap;
                cost = anomaly.MapId == from ? 0 : CostBetween(from, anomaly.MapId);
                what = $"anomaly «{anomaly.Name}» (subarea {anomaly.SubAreaId})";
            }
            else if (kind == Zaapis.Kind)
            {
                // A zaapi destination is NOT in the zaaps table -- they are workshops and marketplaces --
                // so a waypoint cannot be required here. Requiring it was the bug that made zaapi
                // travel go nowhere.
                target = chosen;
                cost = Zaapis.Cost;
                what = $"zaapi to map {target}";
            }
            else
            {
                target = chosen;
                var waypoint = Interactives.WaypointOf(target);
                if (waypoint == null)
                {
                    Console.WriteLine($"[Zaap] The client asks to travel to {target}, which has no zaap.");
                    return;
                }
                cost = CostBetween(from, target);
                what = $"zaap {waypoint.Id}";
            }

            if (MapManager.GetMapInfo(target) == null)
            {
                Console.WriteLine($"[Zaap] Map {target} is not in the world data. No travelling.");
                return;
            }

            if (Jondo.Unity.Server.Network.SessionContext.State.Kamas < cost)
            {
                Console.WriteLine($"[Zaap] Not enough kamas to go to {target}: it costs {cost} and there are " +
                                  $"{Jondo.Unity.Server.Network.SessionContext.State.Kamas}.");
                return;
            }

            long mapaQueDeja = Jondo.Unity.Server.Network.SessionContext.State.MapId;
            Jondo.Unity.Server.Network.SessionContext.State.Kamas -= cost;
            Jondo.Unity.Server.Network.SessionContext.State.MapId = target;

            // One arrives beside the zaap -- or the zaapi -- one travels to, in front of it, as the
            // captures land: the nearest walkable cell by row and column put the traveller to its
            // side or behind it, hidden by its drawing, and beside cell 0 where the map has none.
            var zaap = Interactives.ZaapElements(target);
            var arrival = zaap.Count > 0 ? zaap[0] : Zaapis.ElementsOn(target).FirstOrDefault();
            Jondo.Unity.Server.Network.SessionContext.State.CellId = arrival.Id != 0
                ? FightHandler.CellBeside(target, arrival.Cell)
                : MapManager.GetNearestWalkableCell(target, TeleportHandler.MapCentre);
            DatabaseManager.SaveCurrentCharacter();

            // A zaap ends the party following him, and his followers are told before the old map
            // is: the empty imk and then the kmu, frames 245-246 of "Grupos/con grupo seguir
            // desplazamiento del lider...". See PartyFollowHandler.
            await PartyFollowHandler.LeaderTravelledAsync(SessionContext.Current);

            // And let both maps find out: the zaap told neither.
            await SessionRegistry.AnunciarMudanzaAsync(SessionContext.Current, mapaQueDeja);

            // The same order as the capture: first the character is taken off the map he leaves,
            // then he is told to load the new one, and the kamas at the end.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildActorLeft(Jondo.Unity.Server.Network.SessionContext.State.CharacterId));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildLoadMap(target));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildMapClock());
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildMapDiscovered(target));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ivf, ConnectionProtocol.BuildKamas(Jondo.Unity.Server.Network.SessionContext.State.Kamas)));

            // And close his window, which does not close by itself. In the capture the kld goes out
            // here, between the kamas and the new map's jss.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Kld, ConnectionProtocol.BuildDialogClosed()));

            SessionContext.State.OpenZaapMapId = 0;
            Console.WriteLine($"[Zaap] Travel to {target} ({what}), cell " +
                              $"{Jondo.Unity.Server.Network.SessionContext.State.CellId}, {cost} kamas. Waiting for the jrh.");
        }

        /// <summary>
        /// The destinations he is offered. All the active zaaps: in this emulator the character has
        /// discovered all of them.
        ///
        /// With one condition: that one can come back from the destination. A map whose zaap we do not know
        /// the location of is a place there is no way out of, and that is worse than not offering it. The
        /// place one is already at is offered all the same, because in the real capture one's own map
        /// appears in the list with cost zero.
        /// </summary>
        private static List<ConnectionProtocol.ZaapDestination> Destinations(long from)
        {
            var salida = new List<ConnectionProtocol.ZaapDestination>();
            foreach (var waypoint in Interactives.Waypoints)
            {
                if (!waypoint.Activated) continue;
                if (MapManager.GetMapInfo(waypoint.MapId) == null) continue;
                if (waypoint.MapId != from && !Interactives.CanLeaveFrom(waypoint.MapId)) continue;

                salida.Add(new ConnectionProtocol.ZaapDestination(
                    waypoint.MapId,
                    waypoint.SubAreaId,
                    Interactives.LevelOfSubArea(waypoint.SubAreaId),
                    waypoint.MapId == from ? 0 : CostBetween(from, waypoint.MapId)));
            }

            // And after them, the anomalies tab. They go in this same list: what separates them is
            // each entry's f3, not a separate message.
            salida.AddRange(AnomalyDestinations(from));
            return salida;
        }

        /// <summary>
        /// Only the anomalies tab.
        ///
        /// The level is that of ITS subarea -- the zone the anomaly recreates, which is almost never the
        /// zone where it surfaces -- and that is why it does not go through the destination map's
        /// LevelOfSubArea. It was checked on all sixteen: it fits all sixteen.
        ///
        /// It is charged by where the vestige is, not by where one ends up: in the capture the six anomaly
        /// costs are identical to those of the normal zaap to that same map.
        /// </summary>
        private static List<ConnectionProtocol.ZaapDestination> AnomalyDestinations(long from)
        {
            var salida = new List<ConnectionProtocol.ZaapDestination>();
            if (MapManager.GetMapInfo(Anomalies.ArrivalMap) == null) return salida;

            foreach (var anomaly in Anomalies.All)
            {
                if (MapManager.GetMapInfo(anomaly.MapId) == null) continue;
                salida.Add(new ConnectionProtocol.ZaapDestination(
                    anomaly.MapId,
                    anomaly.SubAreaId,
                    anomaly.Level,
                    anomaly.MapId == from ? 0 : CostBetween(from, anomaly.MapId),
                    Anomalies.Kind,
                    Anomalies.MinutesLeft(anomaly.SubAreaId),
                    Anomalies.Duration));
            }
            return salida;
        }

        private static long CostBetween(long from, long to)
        {
            var a = MapManager.GetMapInfo(from);
            var b = MapManager.GetMapInfo(to);
            if (a == null || b == null || from == to) return 0;

            long steps = Math.Abs(a.PosX - b.PosX) + Math.Abs(a.PosY - b.PosY);
            return Math.Clamp(steps * CostPerStep, MinimumCost, MaximumCost);
        }
    }
}
