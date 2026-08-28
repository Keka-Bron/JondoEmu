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
    /// Usar un zaapi.
    ///
    /// Por el cable es EXACTAMENTE el mismo baile que el zaap —iwo, iwn, hjj para la lista; hjc para
    /// elegir— y por eso el viaje en sí lo hace <see cref="ZaapTravelHandler.TravelAsync"/>: es el
    /// mismo hjc y el mismo mapa de destino, así que duplicarlo sería tener dos sitios donde
    /// arreglar el mismo fallo.
    ///
    /// Lo que cambia es sólo la lista que se ofrece y lo que cuesta:
    ///
    ///   el zaap    lleva a cualquier zaap activado del mundo, y cobra por distancia
    ///   el zaapi   lleva a los sitios de SU ciudad, y cobra 20 kamas fijos
    ///
    /// Los 20 son de la captura, iguales en los 24 destinos de Bonta y en los 21 de Brakmar.
    /// </summary>
    public static class ZaapiTravelHandler
    {
        /// <summary>
        /// Ha clicado el zaapi: se le dice que el elemento está en uso y se le manda su lista.
        ///
        /// Si el mapa no pertenece a ninguna red conocida no se contesta con una lista vacía: se
        /// escribe por qué y se deja el elemento sin abrir. Una ventana vacía parece un fallo del
        /// juego; no abrirla al menos se puede leer en el registro.
        /// </summary>
        public static async Task OpenAsync(NetworkStream stream, Interactives.Element zaapi, int skillId)
        {
            long here = SessionContext.State.MapId;

            var network = Zaapis.NetworkOn(here);
            if (network == null || network.Destinations.Count == 0)
            {
                Console.WriteLine($"[Zaapis] El mapa {here} tiene zaapi pero no hay red cargada para él.");
                return;
            }

            SessionContext.State.OpenZaapMapId = here;

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iwn, ConnectionProtocol.BuildElementInUse(
                    zaapi.Id, skillId, SessionContext.State.CharacterId)));

            var destinations = new List<ConnectionProtocol.ZaapDestination>();
            foreach (var destination in network.Destinations)
            {
                // El sitio donde uno ya está sale sin coste, que es lo que hace el servidor real:
                // proto3 se come el cero y el cliente lo enseña como «estás aquí».
                if (MapManager.GetMapInfo(destination.MapId) == null) continue;
                destinations.Add(new ConnectionProtocol.ZaapDestination(
                    destination.MapId,
                    destination.SubAreaId,
                    Interactives.LevelOfSubArea(destination.SubAreaId),
                    destination.MapId == here ? 0 : Zaapis.Cost,
                    Zaapis.Kind));
            }

            // Sin el f2: la lista del zaapi no lo lleva en ninguna de las tres capturas, mientras
            // que la del zaap lo lleva siempre y con el MISMO valor se mueva uno donde se mueva
            // —73400320 en ocho capturas desde sitios distintos—, o sea que no es «dónde estás»
            // sino el zaap guardado del personaje. Eso aquí no existe todavía, así que en la lista
            // del zaap se sigue mandando el mapa de donde sales; en la del zaapi, nada, que es lo
            // que hace el servidor real.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Hjj, ConnectionProtocol.BuildZaapList(
                    0, destinations, Zaapis.Teleporter)));

            Console.WriteLine($"[Zaapis] {network.City}: {destinations.Count} destinos desde el mapa {here}.");
        }
    }
}
