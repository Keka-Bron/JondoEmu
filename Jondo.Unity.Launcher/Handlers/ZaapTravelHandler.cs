using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Managers;
using Jondo.Unity.Launcher.Network;

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

            SessionContext.State.OpenZaapMapId = here;

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("iwn", ConnectionProtocol.BuildElementInUse(
                    zaap.Id, skillId, Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId)));

            var destinations = Destinations(here);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("hjj", ConnectionProtocol.BuildZaapList(here, destinations)));

            Console.WriteLine($"[Zaap] Abierto en el mapa {here}: {destinations.Count} destinos.");
        }

        /// <summary>
        /// El botón de cerrar. El cliente manda un kla vacío y se queda esperando: la ventana no
        /// se cierra hasta que el servidor contesta.
        /// </summary>
        public static async Task CloseAsync(NetworkStream stream)
        {
            SessionContext.State.OpenZaapMapId = 0;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("kld", ConnectionProtocol.BuildDialogClosed()));
        }

        /// <summary>El cliente ha elegido destino. Se le cobra y se le lleva.</summary>
        public static async Task TravelAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? hjc = ConnectionProtocol.ReadPayload(payload, "hjc");
            if (hjc == null) return;

            long target = 0;
            foreach (var field in ProtoMessage.Parse(hjc).Fields)
            {
                if (field.FieldNumber == 3 && field.WireType == 0) target = field.VarIntValue;
            }
            if (target <= 0) return;

            var waypoint = Interactives.WaypointOf(target);
            if (waypoint == null)
            {
                Console.WriteLine($"[Zaap] El cliente pide viajar a {target}, que no tiene zaap.");
                return;
            }

            if (MapManager.GetMapInfo(target) == null)
            {
                Console.WriteLine($"[Zaap] El mapa {target} no está en los datos del mundo. No se viaja.");
                return;
            }

            long cost = CostBetween(SessionContext.State.OpenZaapMapId != 0
                ? SessionContext.State.OpenZaapMapId
                : Jondo.Unity.Launcher.Network.SessionContext.State.MapId, target);
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
                ConnectionProtocol.BuildMapDiscovered(target));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("ivf", ConnectionProtocol.BuildKamas(Jondo.Unity.Launcher.Network.SessionContext.State.Kamas)));

            // Y cerrarle la ventana, que no se cierra sola. En la captura el kld sale aquí, entre
            // los kamas y el jss del mapa nuevo.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("kld", ConnectionProtocol.BuildDialogClosed()));

            SessionContext.State.OpenZaapMapId = 0;
            Console.WriteLine($"[Zaap] Viaje a {target} (zaap {waypoint.Id}), casilla " +
                              $"{Jondo.Unity.Launcher.Network.SessionContext.State.CellId}, {cost} kamas. Esperando el jrh.");
        }

        /// <summary>
        /// Los destinos que se le ofrecen. Todos los zaaps activos: en este emulador el personaje
        /// los tiene todos descubiertos.
        ///
        /// Con una condición: que del destino se pueda volver. Un mapa cuyo zaap no sepamos dónde
        /// está es un sitio del que no se sale, y eso es peor que no ofrecerlo. El sitio donde uno
        /// ya está se ofrece igualmente, porque en la captura real el mapa propio sale en la lista
        /// con coste cero.
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
