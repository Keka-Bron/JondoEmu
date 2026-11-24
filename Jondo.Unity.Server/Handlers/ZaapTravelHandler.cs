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
        /// Measured on the official server (2026-11-21, a packet capture of the zaap destination list
        /// opened from [9,-20]: 32 destinations, each with its price): ten kamas per whole step of the
        /// straight line between the two maps' world coordinates, floor(√(dx² + dy²)) × 10 -- 29 of the
        /// 32 exactly (40 to Astrub [5,-18], 890 to Frigost [-78,-41]; no ceiling) -- and the other
        /// three are Incarnam's, the same plus 1000 (1160, 1200, 1190). The previous formula, the sum of
        /// |dx| and |dy| capped at 1000, matched 1 of the 32. See ZaapMeasuredCostTests.
        /// </summary>
        private const int MinimumCost = 10;
        private const int CostPerStep = 10;
        private const int IncarnamSurcharge = 1000;
        private const int IncarnamArea = 45;

        /// <summary>
        /// Frigost Island (area 48) is closed below level 50: Captain Ardier refuses the crossing
        /// (dialogues.json, «PL>49»), and on a server that offers every zaap without discovering it
        /// (ServerSettings.RequireZaapDiscovery off) its zaaps would let anyone in. They are neither
        /// offered nor travelled to below that level.
        /// </summary>
        internal const int FrigostArea = 48;
        internal const int FrigostLevel = 50;

        internal static bool ClosedToLevel(int subAreaId, int level)
            => level < FrigostLevel && Interactives.AreaOfSubArea(subAreaId) == FrigostArea;

        /// <summary>
        /// hip: the zaap window's «Guardar», the position saved at the zaap whose window is open,
        /// «Posición guardada.» and the list again with it as the saved one (SavedPositions).
        /// </summary>
        public static async Task SaveAsync(NetworkStream stream)
        {
            var state = SessionContext.State;
            long here = state.OpenZaapMapId != 0 ? state.OpenZaapMapId : state.MapId;
            if (Interactives.ZaapElements(here).Count == 0) return;

            Managers.SavedPositions.Save(state.CharacterId, here);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(Managers.SavedPositions.SavedMessage)));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Hjj, ConnectionProtocol.BuildZaapList(here, Destinations(here))));
            Console.WriteLine($"[Zaap] {state.CharacterName} saves his position at map {here}.");
        }

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
            var session = SessionContext.Current;
            if (!vestige && ZaapDiscoveries.Discover(session.CharacterId, here))
                await EntryEnvironmentMessages.Live.SendKnownAsync(session);
            var destinations = vestige ? AnomalyDestinations(here) : Destinations(here);

            // f2 is the character's saved zaap, the same wherever the window is opened (eight captures).
            long saved = FightHandler.SavePointOf(SessionContext.State.CharacterId).Map;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Hjj, ConnectionProtocol.BuildZaapList(saved, destinations)));

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
            if (await ConquestHandler.HoldsInAsync(stream, ConquestHandler.NoTeleportInAWar)) return;

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
            int anomalyArrival = -1;

            if (kind == Anomalies.Kind)
            {
                if (!Anomalies.TryGet((int)chosen, out var anomaly))
                {
                    Console.WriteLine($"[Anomalies] The client asks for subarea {chosen} and it is not " +
                                      "in the list. No travelling.");
                    return;
                }

                // It is charged by where the vestige is, not by where one ends up: in the capture the
                // six anomaly costs are identical to those of the normal zaap to that same map. One
                // ends up at the entry of the place the anomaly opens on (AnomalyHandler).
                (target, anomalyArrival) = AnomalyHandler.Travel(anomaly.SubAreaId);
                cost = anomaly.MapId == from ? 0 : CostBetween(from, anomaly.MapId);
                what = $"anomaly «{anomaly.Name}» (subarea {anomaly.SubAreaId})";
            }
            else if (kind == PrismProtocol.DestinationKind)
            {
                // One of the alliance's prisms, from the list a prism of its own gave (PrismHandler).
                if (!PrismHandler.IsDestination(SessionContext.State.CharacterId, chosen))
                {
                    Console.WriteLine($"[Prism] The client asks to travel to {chosen}, which holds no prism of his.");
                    return;
                }
                target = chosen;
                cost = CostBetween(from, target);
                what = $"prism on map {target}";
            }
            else if (kind == Zaapis.Kind)
            {
                // A zaapi destination is NOT in the zaaps table -- they are workshops and marketplaces --
                // so a waypoint cannot be required here. Requiring it was the bug that made zaapi
                // travel go nowhere.
                target = chosen;
                if (Transports.On(from) is { } kart && Transports.StopAt(kart.Network, target) != null)
                {
                    // A kart's stop: along its network, at its fare (Transports).
                    cost = target == kart.Stop.MapId ? 0 : kart.Network.Cost;
                    what = $"«{kart.Network.Name}» to map {target}";
                }
                else
                {
                    cost = Zaapis.Cost;
                    what = $"zaapi to map {target}";
                }
            }
            else
            {
                target = chosen;
                var waypoint = Interactives.WaypointOf(target);
                if (waypoint is not { Activated: true } ||
                    (target != from && !ZaapDiscoveries.KnownMaps(SessionContext.State.CharacterId).Contains(target)))
                {
                    Console.WriteLine($"[Zaap] Destination {target} is unavailable to {SessionContext.State.CharacterId}.");
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

            if (ClosedToLevel(DatabaseManager.SubAreaOfMap(target), SessionContext.State.CharacterLevel))
            {
                Console.WriteLine($"[Zaap] Map {target} is in Frigost and {SessionContext.State.CharacterName} " +
                                  $"is below level {FrigostLevel}. No travelling.");
                return;
            }

            if (Jondo.Unity.Server.Network.SessionContext.State.Kamas < cost)
            {
                Console.WriteLine($"[Zaap] Not enough kamas to go to {target}: it costs {cost} and there are " +
                                  $"{Jondo.Unity.Server.Network.SessionContext.State.Kamas}.");
                return;
            }

            long mapaQueDeja = Jondo.Unity.Server.Network.SessionContext.State.MapId;
            var previousMap = SessionContext.Current.Map;
            Jondo.Unity.Server.Network.SessionContext.State.Kamas -= cost;
            Jondo.Unity.Server.Network.SessionContext.State.MapId = target;

            // One arrives beside the zaap -- or the zaapi -- one travels to, in front of it, as the
            // captures land: the nearest walkable cell by row and column put the traveller to its
            // side or behind it, hidden by its drawing, and beside cell 0 where the map has none.
            // A zaapi to a map that carries both must arrive at the zaapi, not at the zaap.
            var arrival = ArrivalOn(target, kind);
            if (arrival.Id == 0 && Transports.On(target) is { } stop && stop.Stop.MapId == target)
                arrival = stop.Stop.Vehicle;
            Jondo.Unity.Server.Network.SessionContext.State.CellId = anomalyArrival > 0
                ? anomalyArrival
                : arrival.Id != 0
                    ? FightHandler.CellBeside(target, arrival.Cell)
                    : MapManager.GetNearestWalkableCell(target, TeleportHandler.MapCentre);
            DatabaseManager.SaveCurrentCharacter();

            // A zaap ends the party following him, and his followers are told before the old map
            // is: the empty imk and then the kmu, frames 245-246 of "Grupos/con grupo seguir
            // desplazamiento del lider...". See PartyFollowHandler.
            await PartyFollowHandler.LeaderTravelledAsync(SessionContext.Current);

            // And let both maps find out: the zaap told neither.
            await SessionRegistry.AnunciarMudanzaAsync(SessionContext.Current, previousMap);

            // The same order as the capture: first the character is taken off the map he leaves,
            // then he is told to load the new one, and the kamas at the end.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildActorLeft(Jondo.Unity.Server.Network.SessionContext.State.CharacterId));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildLoadMap(target));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildMapClock());
            await EntryEnvironmentMessages.SendDiscoveredAsync(SessionContext.Current, target);
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
        /// Active, discovered zaaps that have a usable departure element.
        /// The current zaap is included at zero cost.
        /// </summary>
        private static List<ConnectionProtocol.ZaapDestination> Destinations(long from)
        {
            var salida = new List<ConnectionProtocol.ZaapDestination>();
            var known = ZaapDiscoveries.KnownMaps(SessionContext.State.CharacterId).ToHashSet();
            int level = SessionContext.State.CharacterLevel;
            foreach (var waypoint in Interactives.Waypoints)
            {
                if (!waypoint.Activated) continue;
                if (MapManager.GetMapInfo(waypoint.MapId) == null) continue;
                if (waypoint.MapId != from && !known.Contains(waypoint.MapId)) continue;
                if (waypoint.MapId != from && ClosedToLevel(waypoint.SubAreaId, level)) continue;

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

        /// <summary>
        /// What a zaap trip to a map costs from here (hir { f2: the map }), answered him { f1: the
        /// map, f2: the kamas }, nothing in f2 for the map one stands on: the client asks it on
        /// arriving on a map and while its autopilot plans a route, as the "feria del troll" and
        /// "autopilotaje" captures show (165153537 at 50, then without a price once there).
        /// </summary>
        public static Task TellCostAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? hir = ConnectionProtocol.ReadPayload(payload, "hir");
            if (hir == null) return Task.CompletedTask;
            long map = 0;
            foreach (var field in ProtoMessage.Parse(hir).Fields)
                if (field.FieldNumber == 2 && field.WireType == 0) map = field.VarIntValue;
            if (map <= 0) return Task.CompletedTask;
            long cost = CostBetween(Jondo.Unity.Server.Network.SessionContext.State.MapId, map);
            return Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("him", Pb.New().Var(1, map).VarIfNotZero(2, cost).Build()));
        }

        /// <summary>
        /// The element one lands beside: the zaapi one travelled to when it is a zaapi, even on a map
        /// that also carries a zaap; otherwise the zaap, with the zaapi as the fallback.
        /// </summary>
        internal static Interactives.Element ArrivalOn(long target, int kind)
        {
            var zaap = Interactives.ZaapElements(target);
            var zaapi = Zaapis.ElementsOn(target);
            if (kind == Zaapis.Kind)
                return zaapi.Count > 0 ? zaapi[0] : zaap.FirstOrDefault();
            return zaap.Count > 0 ? zaap[0] : zaapi.FirstOrDefault();
        }

        internal static long CostBetween(long from, long to)
        {
            var a = MapManager.GetMapInfo(from);
            var b = MapManager.GetMapInfo(to);
            if (a == null || b == null || from == to) return 0;

            long dx = a.PosX - b.PosX, dy = a.PosY - b.PosY;
            long steps = (long)Math.Floor(Math.Sqrt(dx * dx + dy * dy));
            long cost = Math.Max(steps * CostPerStep, MinimumCost);
            bool toIncarnam = Interactives.AreaOfSubArea(b.SubAreaId) == IncarnamArea;
            // Measured only from outside Incarnam; inside it the plain distance is kept (not measured).
            bool fromIncarnam = Interactives.AreaOfSubArea(a.SubAreaId) == IncarnamArea;
            return toIncarnam && !fromIncarnam ? cost + IncarnamSurcharge : cost;
        }
    }
}
