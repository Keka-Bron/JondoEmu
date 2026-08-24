using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Managers;
using Jondo.Unity.Launcher.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Launcher.Handlers
{
    /// <summary>
    /// Entrar y salir de una casa.
    ///
    /// Las dos mitades son distintas por el cable, y ésa fue la sorpresa. Medido de las capturas
    /// «entrar en mi casa» y «desde dentro de casa salir a fuera»:
    ///
    ///   entrar   iwo { f1: habilidad, f2: elemento, f3: vivienda }
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
        /// <summary>
        /// Resolves the return for an interior's map-exit cell.  Some 3.6 maps expose that cell
        /// as a map-boundary request (<c>jqi</c>) rather than an <c>iwo</c> interactive.  The
        /// client only emits jqi after its own pathing reaches that authored cell; the server
        /// additionally confines the destination to the door used by this session (or the
        /// catalogue's deterministic way-back after reconnecting inside).
        /// </summary>
        public static bool TryResolveBoundaryExit(out long targetMapId, out int targetCellId,
                                                   out int entryElementId)
        {
            targetMapId = 0;
            targetCellId = 0;
            entryElementId = 0;

            long interior = SessionContext.State.MapId;
            if (!Houses.IsInterior(interior)) return false;

            long enteredFrom = SessionContext.State.HouseEntryMapId;
            int enteredCell = SessionContext.State.HouseEntryCell;
            Houses.Door? fallback = null;
            if (enteredFrom <= 0 || enteredFrom == interior || MapManager.GetMapInfo(enteredFrom) == null)
            {
                if (!Houses.TryGetWayBack(interior, out var knownDoor)) return false;
                fallback = knownDoor;
                enteredFrom = knownDoor.MapId;
                enteredCell = knownDoor.Cell;
            }

            if (MapManager.GetMapInfo(enteredFrom) == null) return false;
            targetMapId = enteredFrom;
            targetCellId = MapManager.GetNearestWalkableCell(enteredFrom, enteredCell);
            entryElementId = fallback?.ElementId ?? 0;
            return targetCellId >= 0;
        }

        /// <summary>Clears only after the shared map-change sequence accepted the destination.</summary>
        public static void CompleteBoundaryExit()
        {
            SessionContext.State.ClearPendingHousePurchase();
            SessionContext.State.HouseEntryMapId = 0;
            SessionContext.State.HouseEntryCell = 0;
            SessionContext.State.OpenHouseId = 0;
        }

        /// <summary>El cliente ha clicado la puerta de la calle.</summary>
        public static async Task EnterAsync(NetworkStream stream, int elementId, int skillId,
                                            int requestedHouseId)
        {
            SessionContext.State.ClearPendingHousePurchase();
            long here = SessionContext.State.MapId;
            if (!Houses.TryGetDoor(here, elementId, out var door))
            {
                Console.WriteLine($"[Casas] Puerta desconocida: mapa {here}, elemento {elementId}.");
                return;
            }

            if (!HouseManager.TryResolveDoor(here, elementId, requestedHouseId, out var house) ||
                house == null)
            {
                Console.WriteLine($"[Houses] Rejected dwelling {requestedHouseId} at door " +
                                  $"{elementId} on map {here}.");
                return;
            }
            if (!HouseManager.IsWithinInteractionRange(SessionContext.State.CellId, house.CellId))
            {
                Console.WriteLine($"[Houses] Rejected remote door use for house {house.Id}: " +
                                  $"character at {SessionContext.State.CellId}, door at {house.CellId}.");
                return;
            }
            if (!HouseManager.CanEnter(house, SessionContext.Current.AccountId))
            {
                Console.WriteLine($"[Houses] Account {SessionContext.Current.AccountId} may not " +
                                  $"enter house {house.Id}.");
                return;
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.InteractiveUsedMessage, ConnectionProtocol.BuildElementInUse(
                    elementId, skillId, SessionContext.State.CharacterId)));

            long mapaQueDeja = here;
            SessionContext.State.HouseEntryMapId = here;
            SessionContext.State.HouseEntryCell = SessionContext.State.CellId;
            SessionContext.State.OpenHouseId = house.Id;
            SessionContext.State.MapId = house.InteriorMapId;

            // Se entra al lado de la salida, no encima: así el primer clic para salir cae cerca.
            int destino = Houses.TryGetExit(house.InteriorMapId, out var exit) ? exit.Cell : 0;
            SessionContext.State.CellId = MapManager.GetNearestWalkableCell(house.InteriorMapId, destino);
            DatabaseManager.SaveCurrentCharacter();

            await SessionRegistry.AnunciarMudanzaAsync(SessionContext.Current, mapaQueDeja);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildActorLeft(SessionContext.State.CharacterId));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.HouseEnterMapMessage, Pb.New().Var(1, house.InteriorMapId).Build()));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildMapClock());

            Console.WriteLine($"[Casas] Entrada por la puerta {elementId} del mapa {mapaQueDeja} " +
                               $"a la casa {house.Id}, interior {house.InteriorMapId}, " +
                               $"casilla {SessionContext.State.CellId}.");
        }

        /// <summary>
        /// Skill 97 only opens the client's confirmation UI. The actual price is sent later in
        /// <c>jal</c>; no ownership or kamas changes are allowed during this generic iwo click.
        /// </summary>
        public static async Task OpenPurchaseAsync(NetworkStream stream, int elementId, int skillId,
                                                   int requestedHouseId)
        {
            SessionContext.State.ClearPendingHousePurchase();
            long here = SessionContext.State.MapId;
            // Skill 97 is tied to the registered door.  iwo.f3 is not the purchase identity and
            // may be absent/stale, so the pending context is derived only from map + element.
            if (!HouseManager.TryGetByDoor(here, elementId, out var house) ||
                house == null ||
                !HouseManager.CanPurchaseFirstHand(house, SessionContext.Current.AccountId) ||
                !HouseManager.IsWithinInteractionRange(SessionContext.State.CellId, house.CellId) ||
                SessionContext.State.CharacterId <= 0)
            {
                Console.WriteLine($"[Houses] Invalid purchase dialog request at {here}/{elementId} " +
                                  $"for dwelling {requestedHouseId}.");
                return;
            }

            SessionContext.State.PendingHousePurchase = new PendingHousePurchaseContext
            {
                HouseId = house.Id,
                MapId = here,
                ElementId = elementId,
                ExpectedPrice = house.Price,
                ExpectedOwnerAccountId = house.OwnerAccountId,
                ExpectedListed = house.Listed,
                AccountId = SessionContext.Current.AccountId,
                CharacterId = SessionContext.State.CharacterId,
            };
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.InteractiveUsedMessage,
                    ConnectionProtocol.BuildElementInUse(
                        elementId, skillId, SessionContext.State.CharacterId)));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.PurchasableDialogEvent,
                    ConnectionProtocol.BuildPurchasableDialog(house)));
            Console.WriteLine($"[Houses] Purchase dialog opened for house {house.Id} " +
                              $"at {house.Price} kamas.");
        }

        /// <summary>
        /// Confirms the price displayed by <c>khr</c>.  The request contains only that price;
        /// house identity and the complete offer snapshot come from session state.
        /// </summary>
        public static async Task ConfirmPurchaseAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? jal = ConnectionProtocol.ReadPayload(payload, Op.HouseBuyRequestMessage);
            if (jal == null) return;

            long proposedPrice = -1;
            foreach (var field in ProtoMessage.Parse(jal).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 0) proposedPrice = field.VarIntValue;
            }

            var pending = SessionContext.State.PendingHousePurchase;
            SessionContext.State.ClearPendingHousePurchase(); // confirmations are one-shot
            if (pending == null || proposedPrice <= 0 ||
                pending.AccountId != SessionContext.Current.AccountId ||
                pending.CharacterId != SessionContext.State.CharacterId ||
                pending.MapId != SessionContext.State.MapId ||
                !HouseManager.TryGetByDoor(pending.MapId, pending.ElementId, out var liveHouse) ||
                liveHouse == null || liveHouse.Id != pending.HouseId ||
                !HouseManager.IsWithinInteractionRange(
                    SessionContext.State.CellId, liveHouse.CellId) ||
                proposedPrice != pending.ExpectedPrice)
            {
                Console.WriteLine("[Houses] Rejected jal without a matching live purchase offer.");
                return;
            }

            var result = HouseManager.TryPurchase(
                pending.HouseId,
                pending.MapId,
                pending.ElementId,
                pending.AccountId,
                pending.CharacterId,
                pending.ExpectedOwnerAccountId,
                pending.ExpectedListed,
                pending.ExpectedPrice,
                out long paid,
                out long remainingKamas);

            if (result != HousePurchaseResult.Success)
            {
                Console.WriteLine($"[Houses] Purchase of house {pending.HouseId} rejected: {result}.");
                return;
            }

            SessionContext.State.Kamas = remainingKamas;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.KamasUpdateMessage,
                    ConnectionProtocol.BuildKamas(SessionContext.State.Kamas)));

            // There is no evidenced house-success packet in the pinned client: jam belongs to a
            // haven-bag-adjacent owner.  The next normal jss/map refresh carries the new lnx owner
            // state, while ivf above immediately synchronizes the paid balance.
            Console.WriteLine($"[Houses] Account {pending.AccountId} bought house " +
                              $"{pending.HouseId} for {paid} kamas.");
        }

        /// <summary>El cliente ha clicado la puerta de dentro.</summary>
        public static async Task LeaveAsync(NetworkStream stream, int elementId, int skillId)
        {
            SessionContext.State.ClearPendingHousePurchase();
            long here = SessionContext.State.MapId;

            if (!Houses.TryGetExit(here, out var clickedExit) ||
                clickedExit.ElementId != elementId ||
                !HouseManager.IsWithinInteractionRange(
                    SessionContext.State.CellId, clickedExit.Cell))
            {
                Console.WriteLine($"[Houses] Rejected remote or mismatched interior exit use at " +
                                  $"{here}/{elementId} from cell {SessionContext.State.CellId}.");
                return;
            }

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
                ConnectionProtocol.Push(Op.InteractiveUsedMessage, ConnectionProtocol.BuildElementInUse(
                    elementId, skillId, SessionContext.State.CharacterId)));

            SessionContext.State.MapId = salidaMapa;
            SessionContext.State.CellId = MapManager.GetNearestWalkableCell(salidaMapa, salidaCasilla);
            SessionContext.State.HouseEntryMapId = 0;
            SessionContext.State.HouseEntryCell = 0;
            SessionContext.State.OpenHouseId = 0;
            DatabaseManager.SaveCurrentCharacter();
            ZaapDiscovery.DiscoverOnArrival(SessionContext.State.CharacterId, salidaMapa);

            await SessionRegistry.AnunciarMudanzaAsync(SessionContext.Current, here);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildActorLeft(SessionContext.State.CharacterId));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildLoadMap(salidaMapa));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildMapClock());
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildKnownZaaps(SessionContext.State.CharacterId));

            Console.WriteLine($"[Casas] Salida del interior {here} al mapa {salidaMapa}, " +
                              $"casilla {SessionContext.State.CellId}.");
        }
    }
}
