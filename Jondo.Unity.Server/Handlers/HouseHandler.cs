using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Entrar y salir de una casa.
    ///
    /// Las dos mitades son distintas por el cable, y ésa fue la sorpresa. Medido de las capturas
    /// «entrar en mi casa» y «desde dentro de casa salir a fuera»:
    ///
    ///   entrar   iwo { f1: habilidad, f2: elemento, f3: instancia }
    ///            iwn { f1: 1, f2: elemento, f4: 84, f5: personaje }
    ///            jqw { f1: mapa interior }
    ///
    ///   salir    iwo { f1: habilidad, f2: elemento }
    ///            iwn { f1: 1, f2: elemento, f4: 184, f5: personaje }
    ///            jru { f2: mapa de la calle }
    ///
    /// El mapa viaja en el campo 1 del jqw y en el campo 2 del jru. Mandar un jru para entrar
    /// haría que el cliente cargase el mapa sin saber que está entrando en una vivienda.
    ///
    /// Después de cualquiera de los dos el cliente pide el mapa por su cuenta con kmv y jrh, así
    /// que aquí no se manda el jss: se manda el aviso y se deja que lo pida, que es lo que hace
    /// el servidor real.
    /// </summary>
    public static class HouseHandler
    {
        /// <summary>El cliente ha clicado la puerta de la calle.</summary>
        public static async Task EnterAsync(NetworkStream stream, int elementId, int skillId)
        {
            long here = SessionContext.State.MapId;
            if (!Houses.TryGetDoor(here, elementId, out var door))
            {
                Console.WriteLine($"[Casas] Puerta desconocida: mapa {here}, elemento {elementId}.");
                return;
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iwn, ConnectionProtocol.BuildElementInUse(
                    elementId, skillId, SessionContext.State.CharacterId)));

            long mapaQueDeja = here;
            SessionContext.State.HouseEntryMapId = here;
            SessionContext.State.HouseEntryCell = SessionContext.State.CellId;
            SessionContext.State.MapId = door.InteriorMapId;

            // Se entra al lado de la salida, no encima: así el primer clic para salir cae cerca.
            int destino = Houses.TryGetExit(door.InteriorMapId, out var exit) ? exit.Cell : 0;
            SessionContext.State.CellId = MapManager.GetNearestWalkableCell(door.InteriorMapId, destino);
            DatabaseManager.SaveCurrentCharacter();

            await SessionRegistry.AnunciarMudanzaAsync(SessionContext.Current, mapaQueDeja);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildActorLeft(SessionContext.State.CharacterId));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Jqw, Pb.New().Var(1, door.InteriorMapId).Build()));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildMapClock());

            string cual = door.IsKnown
                ? $"«{door.Name}» ({door.Dwellings} dueños en el juego real)"
                : $"puerta {elementId}";
            Console.WriteLine($"[Casas] Entrada por {cual} del mapa {mapaQueDeja} al interior " +
                              $"{door.InteriorMapId}, casilla {SessionContext.State.CellId}.");
        }

        /// <summary>El cliente ha clicado la puerta de dentro.</summary>
        public static async Task LeaveAsync(NetworkStream stream, int elementId, int skillId)
        {
            long here = SessionContext.State.MapId;

            // Primero por donde se entro; si no se sabe, por donde digan los datos.
            long salidaMapa = SessionContext.State.HouseEntryMapId;
            int salidaCasilla = SessionContext.State.HouseEntryCell;
            if (salidaMapa == 0 || salidaMapa == here)
            {
                if (!Houses.TryGetWayBack(here, out var puerta))
                {
                    Console.WriteLine($"[Casas] El mapa {here} no es interior de ninguna casa conocida.");
                    return;
                }
                salidaMapa = puerta.MapId;
                salidaCasilla = puerta.Cell;
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iwn, ConnectionProtocol.BuildElementInUse(
                    elementId, skillId, SessionContext.State.CharacterId)));

            SessionContext.State.MapId = salidaMapa;
            SessionContext.State.CellId = MapManager.GetNearestWalkableCell(salidaMapa, salidaCasilla);
            SessionContext.State.HouseEntryMapId = 0;
            SessionContext.State.HouseEntryCell = 0;
            DatabaseManager.SaveCurrentCharacter();

            await SessionRegistry.AnunciarMudanzaAsync(SessionContext.Current, here);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildActorLeft(SessionContext.State.CharacterId));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildLoadMap(salidaMapa));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildMapClock());
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildMapDiscovered(salidaMapa));

            Console.WriteLine($"[Casas] Salida del interior {here} al mapa {salidaMapa}, " +
                              $"casilla {SessionContext.State.CellId}.");
        }
    }
}
