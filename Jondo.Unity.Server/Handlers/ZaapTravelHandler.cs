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
    /// Usar un zaap.
    ///
    /// Leído de tres capturas reales —abrir la lista, viajar de Animatopia al Castillo de Amakna, y
    /// el zaapi de Bonta— y son dos pasos:
    ///
    ///   cliente  iwo { f1: uid de la habilidad, f2: elemento }   ha clicado el zaap
    ///   servidor iwn { f1: 1, f2: uid, f4: habilidad, f5: quién }  el elemento está en uso
    ///   servidor hjj { f2: mapa donde está, f3 (repetido): un destino }   la lista
    ///
    ///   cliente  hjc { f3: mapa de destino }                     ha elegido
    ///   servidor jru + el mapa entero + ivf con los kamas que quedan
    ///
    /// Cada destino del hjj es:
    ///
    ///   f1: el nivel de la zona     f2: lo que cuesta ir
    ///   f5: el mapa                 f6: la subzona
    ///
    /// El f6 se comprobó contra MapPositions en las veinticinco entradas de la captura y cuadra en
    /// todas. El destino donde uno ya está viaja sin f2, que es proto3 diciendo que cuesta cero.
    /// </summary>
    /// <remarks>
    /// Se llama ZaapTravelHandler y no ZaapHandler porque ese nombre ya está cogido: en
    /// Network vive el ZaapHandler del launcher, que no tiene nada que ver con esto —es el
    /// servicio Thrift que habla con el cliente antes de entrar al juego.
    /// </remarks>
    public static class ZaapTravelHandler
    {
        /// <summary>
        /// Lo que cuesta viajar, en kamas.
        ///
        /// El servidor real lo calcula por distancia —en la captura van de 170 a 1080 y los
        /// destinos lejanos son los caros— pero la fórmula exacta no está en ningún dato del
        /// cliente. Esta es nuestra, y hace lo mismo: cuesta más cuanto más lejos, con un suelo y
        /// un techo dentro del rango que se ve en la captura.
        /// </summary>
        private const int MinimumCost = 10;
        private const int MaximumCost = 1000;
        private const int CostPerStep = 10;

        /// <summary>El mapa del zaap que está abierto ahora mismo, para cobrar desde el sitio bueno.</summary>
        /// <summary>
        /// El cliente ha clicado el zaap. Se le contesta que el elemento está en uso y se le manda
        /// la lista de destinos.
        /// </summary>
        public static async Task OpenAsync(NetworkStream stream, Interactives.Element zaap, int skillId)
        {
            long here = Jondo.Unity.Launcher.Network.SessionContext.State.MapId;
            long characterId = Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId;

            SessionContext.State.OpenZaapMapId = here;

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.InteractiveUsedMessage, ConnectionProtocol.BuildElementInUse(
                    zaap.Id, skillId, Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId)));

            // A vestige is an anomaly gateway, not an ordinary zaap. Its list contains only the
            // anomaly tab; a normal zaap additionally exposes that tab after ordinary waypoints.
            bool vestige = Interactives.IsVestige(here, zaap);
            var destinations = vestige
                ? AnomalyDestinations(here)
                : Destinations(here, DatabaseManager.GetDiscoveredZaapMaps(characterId));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.TeleportDestinationsMessage, ConnectionProtocol.BuildZaapList(here, destinations)));

            Console.WriteLine($"[{(vestige ? "Vestige" : "Zaap")}] Opened on map {here}: " +
                              $"{destinations.Count} destinations.");
        }

        /// <summary>
        /// El botón de cerrar. El cliente manda un kla vacío y se queda esperando: la ventana no
        /// se cierra hasta que el servidor contesta.
        /// </summary>
        public static async Task CloseAsync(NetworkStream stream)
        {
            SessionContext.State.OpenZaapMapId = 0;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.LeaveDialogMessage, ConnectionProtocol.BuildDialogClosed()));
        }

        /// <summary>El cliente ha elegido destino. Se le cobra y se le lleva.</summary>
        public static async Task TravelAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? hjc = ConnectionProtocol.ReadPayload(payload, Op.TeleportRequestMessage);
            if (hjc == null) return;

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
                : SessionContext.State.MapId;

            long target;
            long cost;
            string destinationLabel;
            if (kind == Anomalies.Kind)
            {
                if (!Anomalies.TryGet((int)chosen, out var anomaly))
                {
                    Console.WriteLine($"[Anomalies] Unknown subarea {chosen}; travel rejected.");
                    return;
                }
                target = Anomalies.ArrivalMap;
                cost = anomaly.MapId == from ? 0 : CostBetween(from, anomaly.MapId);
                destinationLabel = $"anomaly {anomaly.Name} (subarea {anomaly.SubAreaId})";
            }
            else if (kind == Zaapis.Kind)
            {
                // Zaapi destinations include workshops and markets which are not waypoints.
                target = chosen;
                cost = Zaapis.Cost;
                destinationLabel = $"zaapi map {target}";
            }
            else
            {
                target = chosen;
                var waypoint = Interactives.WaypointOf(target);
                if (waypoint == null || !waypoint.Activated || !Interactives.CanLeaveFrom(target))
                {
                    Console.WriteLine($"[Zaap] El cliente pide viajar a {target}, que no es un " +
                                      "destino de zaap activo y utilizable.");
                    return;
                }
                if (!DatabaseManager.HasDiscoveredZaap(SessionContext.State.CharacterId, target))
                {
                    Console.WriteLine($"[Zaap] El personaje {SessionContext.State.CharacterId} " +
                                      $"intentó viajar al zaap no descubierto {target}.");
                    return;
                }
                cost = CostBetween(from, target);
                destinationLabel = $"zaap {waypoint.Id}";
            }

            if (MapManager.GetMapInfo(target) == null)
            {
                Console.WriteLine($"[Zaap] El mapa {target} no está en los datos del mundo. No se viaja.");
                return;
            }

            if (Jondo.Unity.Launcher.Network.SessionContext.State.Kamas < cost)
            {
                Console.WriteLine($"[Zaap] Faltan kamas para ir a {target}: cuesta {cost} y hay " +
                                  $"{Jondo.Unity.Launcher.Network.SessionContext.State.Kamas}.");
                return;
            }

            long mapaQueDeja = Jondo.Unity.Launcher.Network.SessionContext.State.MapId;
            Jondo.Unity.Launcher.Network.SessionContext.State.Kamas -= cost;
            Jondo.Unity.Launcher.Network.SessionContext.State.MapId = target;

            // Se llega al lado del zaap, no encima: la casilla del zaap no se pisa.
            var arrival = Interactives.ZaapElements(target);
            Jondo.Unity.Launcher.Network.SessionContext.State.CellId = MapManager.GetNearestWalkableCell(
                target, arrival.Count > 0 ? arrival[0].Cell : 0);
            DatabaseManager.SaveCurrentCharacter();
            ZaapDiscovery.DiscoverOnArrival(SessionContext.State.CharacterId, target);

            // Y que los dos mapas se enteren: el zaap no avisaba a ninguno.
            await SessionRegistry.AnunciarMudanzaAsync(SessionContext.Current, mapaQueDeja);

            // El mismo orden que la captura: primero se saca al personaje del mapa que deja, luego
            // se le manda cargar el nuevo, y los kamas al final.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildActorLeft(Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildLoadMap(target));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildMapClock());
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildKnownZaaps(SessionContext.State.CharacterId));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.KamasUpdateMessage, ConnectionProtocol.BuildKamas(Jondo.Unity.Launcher.Network.SessionContext.State.Kamas)));

            // Y cerrarle la ventana, que no se cierra sola. En la captura el kld sale aquí, entre
            // los kamas y el jss del mapa nuevo.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.LeaveDialogMessage, ConnectionProtocol.BuildDialogClosed()));

            SessionContext.State.OpenZaapMapId = 0;
            Console.WriteLine($"[Zaap] Viaje a {target} ({destinationLabel}), casilla " +
                              $"{Jondo.Unity.Launcher.Network.SessionContext.State.CellId}, {cost} kamas. Esperando el jrh.");
        }

        /// <summary>
        /// Los destinos que se le ofrecen. Sólo los zaaps que este personaje descubrió al llegar
        /// a su mapa. El catálogo global nunca se convierte en progreso de personaje.
        ///
        /// Con una condición: que del destino se pueda volver. Un mapa cuyo zaap no sepamos dónde
        /// está es un sitio del que no se sale, y eso es peor que no ofrecerlo. El sitio donde uno
        /// ya está se ofrece igualmente, porque en la captura real el mapa propio sale en la lista
        /// con coste cero.
        /// </summary>
        private static List<ConnectionProtocol.ZaapDestination> Destinations(
            long from, IEnumerable<long> discoveredMaps)
        {
            var salida = OrdinaryDestinations(from, discoveredMaps);
            salida.AddRange(AnomalyDestinations(from));
            return salida;
        }

        /// <summary>Pure character-discovery filter, exposed internally to the startup guard.</summary>
        internal static List<ConnectionProtocol.ZaapDestination> OrdinaryDestinations(
            long from, IEnumerable<long> discoveredMaps)
        {
            var salida = new List<ConnectionProtocol.ZaapDestination>();
            var discovered = new HashSet<long>(discoveredMaps);
            foreach (var waypoint in Interactives.Waypoints)
            {
                if (!discovered.Contains(waypoint.MapId)) continue;
                if (!waypoint.Activated) continue;
                if (MapManager.GetMapInfo(waypoint.MapId) == null) continue;
                if (!Interactives.CanLeaveFrom(waypoint.MapId)) continue;

                salida.Add(new ConnectionProtocol.ZaapDestination(
                    waypoint.MapId,
                    waypoint.SubAreaId,
                    Interactives.LevelOfSubArea(waypoint.SubAreaId),
                    waypoint.MapId == from ? 0 : CostBetween(from, waypoint.MapId)));
            }
            return salida;
        }

        /// <summary>Builds the anomaly-only destinations, identified by subarea in the client reply.</summary>
        private static List<ConnectionProtocol.ZaapDestination> AnomalyDestinations(long from)
        {
            var result = new List<ConnectionProtocol.ZaapDestination>();
            if (MapManager.GetMapInfo(Anomalies.ArrivalMap) == null) return result;

            foreach (var anomaly in Anomalies.All)
            {
                if (MapManager.GetMapInfo(anomaly.MapId) == null) continue;
                result.Add(new ConnectionProtocol.ZaapDestination(
                    anomaly.MapId,
                    anomaly.SubAreaId,
                    anomaly.Level,
                    anomaly.MapId == from ? 0 : CostBetween(from, anomaly.MapId),
                    Anomalies.Kind,
                    Anomalies.MinutesLeft(anomaly.SubAreaId),
                    Anomalies.Duration));
            }
            return result;
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
