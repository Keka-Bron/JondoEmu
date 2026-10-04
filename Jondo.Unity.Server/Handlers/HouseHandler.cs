using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// <remarks>
    /// <para>
    /// And everything an owner does with his house, measured on the captures of "Casas/" (frame
    /// numbers are positions in <c>hilo.tramas</c> of each file):
    ///
    ///   put on sale ("poner casa en venta por 20.500.000")
    ///     C iwo (98, "Vender") → S iwn, khr { house, instance, 1, price shown }
    ///     C izv { instance, house } → S izr { full plaque }                         (a response)
    ///     C jan { price, instance, true } + kla
    ///     S iwm (door, off sale), izz (no price), jjt, iwm (door, on sale), izz (price),
    ///       jaa, izu, izz, kld { 3 }
    ///   take off sale ("retirar casa de la venta"): the same from iwo (108, "Modificar el
    ///     precio de venta") and jan { price, instance } without f3; no jjt, and the second
    ///     iwm/izz pair is not there
    ///   access code ("cambiar codigo acceso a 13581321", "modificar codigo acceso a 135813")
    ///     C iwo (100) → S iwn, kia { f3: 8 }; C khv { code } → S iwm, izz, khu {}, kld { 2 }
    ///   chest lock ("poner cerrojo al cofre-codigo 1358", and with nothing typed)
    ///     C iwo (105) → S iwn, kia { f3: 8 }; C khv { code } → S khu {}, kld { 2 }
    ///   chest ("abrir cofre de la casa-mover items-cerrarlo")
    ///     C iwo (104) → S iwn, kci { 100, 4 }, iwb; then any storage's moves
    ///
    /// And a stranger at a locked door (pandala, frames 3882-3890):
    ///     C iwo (84) → S iwn, kia { f1: 1, f3: 8 }; C khw { "1234" } → S kld { 2 }, khu { f2: 1 }
    /// </para>
    /// <para>
    /// Inferred, and said where it is done: buying (no capture buys a house), getting in or
    /// opening a chest with the RIGHT code (only a wrong one was captured), who else on the map is
    /// told of a change, and the refusals.
    /// </para>
    /// </remarks>
    public static class HouseHandler
    {
        /// <summary>A house window a character has open.</summary>
        public enum DialogKind
        {
            /// <summary>The owner's sale window (khr), answered by jan.</summary>
            Sale,

            /// <summary>A buyer's window (khr with f1), answered -- inference -- by jad or jal.</summary>
            Purchase,

            /// <summary>The owner's keypad for the door's code (kia), answered by khv.</summary>
            DoorCode,

            /// <summary>The owner's keypad for a chest's code, answered by khv.</summary>
            ChestCode,

            /// <summary>A stranger's keypad at a locked door (kia f1 1), answered by khw.</summary>
            EnterCode,

            /// <summary>A stranger's keypad at a locked chest, answered by khw.</summary>
            ChestKeypad,
        }

        public sealed class Dialog
        {
            public DialogKind Kind { get; init; }

            /// <summary>The house: its street door.</summary>
            public long DoorMapId { get; init; }
            public int DoorElementId { get; init; }

            /// <summary>The chest, for the chest's keypads.</summary>
            public int ChestElementId { get; init; }

            /// <summary>The price the window showed.</summary>
            public long Price { get; init; }

            /// <summary>Set once its answer has come: the kla that follows closes nothing more.</summary>
            public bool Answered { get; set; }

            /// <summary>The map it was opened on: a window left behind by a jump elsewhere answers nothing.</summary>
            public long OpenedOn { get; init; } = SessionContext.State.MapId;
        }

        /// <summary>This session's house window, if it is still on the map where it was opened.</summary>
        private static Dialog? OpenDialog(params DialogKind[] kinds)
        {
            var dialog = SessionContext.State.HouseDialog;
            if (dialog == null) return null;
            if (dialog.OpenedOn != SessionContext.State.MapId)
            {
                SessionContext.State.HouseDialog = null;
                return null;
            }
            return kinds.Length == 0 || Array.IndexOf(kinds, dialog.Kind) >= 0 ? dialog : null;
        }

        // ─── The door ───────────────────────────────────────────────────────────

        /// <summary>A house door was used: enter, buy, sell or the code, by the skill.</summary>
        public static async Task UseDoorAsync(NetworkStream? stream, RegisteredInteractive door, InteractiveAction action)
        {
            if (!Contains(VisibleActions(door, SessionContext.Current.AccountId), action))
            {
                Console.WriteLine($"[Houses] Skill {action.SkillId} of door {door.Element.Id} is not offered to this character.");
                return;
            }

            switch (action.SkillId)
            {
                case Houses.EnterSkill:
                    await EnterAsync(stream, door.Element.Id, action.SkillId);
                    break;
                case Houses.BuySkill:
                    await OpenPurchaseAsync(stream, door.Element.Id, action.SkillId);
                    break;
                case Houses.SellSkill:
                case Houses.PriceSkill:
                    await OpenSaleAsync(stream, door.Element.Id, action.SkillId);
                    break;
                case Houses.CodeSkill:
                    await OpenDoorKeypadAsync(stream, door.Element.Id, action.SkillId);
                    break;
            }
        }

        /// <summary>El cliente ha clicado la puerta de la calle.</summary>
        public static async Task EnterAsync(NetworkStream? stream, int elementId, int skillId)
        {
            long here = SessionContext.State.MapId;
            if (!Houses.TryGetDoor(here, elementId, out var door))
            {
                Console.WriteLine($"[Casas] Puerta desconocida: mapa {here}, elemento {elementId}.");
                return;
            }

            await SendAsync(stream, Op.Iwn, ConnectionProtocol.BuildElementInUse(
                elementId, skillId, SessionContext.State.CharacterId));

            // A locked house that is not one's own asks for its code first: pandala, frame 3884.
            var house = HouseStore.Of(here, elementId);
            if (house.Owned && house.Locked && house.AccountId != SessionContext.Current.AccountId)
            {
                SessionContext.State.HouseDialog = new Dialog
                {
                    Kind = DialogKind.EnterCode, DoorMapId = here, DoorElementId = elementId,
                };
                await SendAsync(stream, Op.Kia, HouseProtocol.BuildCodeKeypad(toGetIn: true));
                return;
            }

            await GoInsideAsync(stream, door);
        }

        /// <summary>Through the door, once it is open: the part of entering after the iwn.</summary>
        private static async Task GoInsideAsync(NetworkStream? stream, Houses.Door door)
        {
            long mapaQueDeja = SessionContext.State.MapId;
            SessionContext.State.HouseEntryMapId = mapaQueDeja;
            SessionContext.State.HouseEntryCell = SessionContext.State.CellId;
            SessionContext.State.HouseEntryElementId = door.ElementId;
            SessionContext.State.MapId = door.InteriorMapId;

            // Se entra al lado de la salida, no encima: así el primer clic para salir cae cerca.
            int destino = Houses.TryGetExit(door.InteriorMapId, out var exit) ? exit.Cell : 0;
            SessionContext.State.CellId = MapManager.GetNearestWalkableCell(door.InteriorMapId, destino);
            DatabaseManager.SaveCurrentCharacter();

            await SessionRegistry.AnunciarMudanzaAsync(SessionContext.Current, mapaQueDeja);

            await WriteAsync(stream, ConnectionProtocol.BuildActorLeft(SessionContext.State.CharacterId));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jqw, Pb.New().Var(1, door.InteriorMapId).Build()));
            await WriteAsync(stream, ConnectionProtocol.BuildMapClock());

            string cual = door.IsKnown
                ? $"«{door.Name}» ({door.Dwellings} dueños en el juego real)"
                : $"puerta {door.ElementId}";
            Console.WriteLine($"[Casas] Entrada por {cual} del mapa {mapaQueDeja} al interior " +
                              $"{door.InteriorMapId}, casilla {SessionContext.State.CellId}.");
        }

        /// <summary>El cliente ha clicado la puerta de dentro.</summary>
        public static async Task LeaveAsync(NetworkStream? stream, int elementId, int skillId)
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

            await SendAsync(stream, Op.Iwn, ConnectionProtocol.BuildElementInUse(
                elementId, skillId, SessionContext.State.CharacterId));

            SessionContext.State.MapId = salidaMapa;
            SessionContext.State.CellId = MapManager.GetNearestWalkableCell(salidaMapa, salidaCasilla);
            SessionContext.State.HouseEntryMapId = 0;
            SessionContext.State.HouseEntryCell = 0;
            SessionContext.State.HouseEntryElementId = 0;
            DatabaseManager.SaveCurrentCharacter();

            await SessionRegistry.AnunciarMudanzaAsync(SessionContext.Current, here);

            await WriteAsync(stream, ConnectionProtocol.BuildActorLeft(SessionContext.State.CharacterId));
            await WriteAsync(stream, ConnectionProtocol.BuildLoadMap(salidaMapa));
            await WriteAsync(stream, ConnectionProtocol.BuildMapClock());
            await WriteAsync(stream, ConnectionProtocol.BuildMapDiscovered(salidaMapa));

            Console.WriteLine($"[Casas] Salida del interior {here} al mapa {salidaMapa}, " +
                              $"casilla {SessionContext.State.CellId}.");
        }

        // ─── What each viewer is offered ────────────────────────────────────────

        /// <summary>
        /// The skills of a house door or chest this viewer is offered; every other interactive's
        /// are all of them.
        /// </summary>
        /// <remarks>
        /// The door, measured: its owner gets 84 enter, 100 code and 98 sell -- 108 change the price
        /// once on sale -- (jss f11 of "desde dentro de casa salir a fuera", frame 13, and the iwm of
        /// the sale); a stranger at a house that is not on sale gets 84 alone (pandala, frame 3867).
        /// A stranger at a house on sale, or at one nobody owns, gets 84 and 97 "Comprar": the
        /// client's skill for it, an inference.
        ///
        /// The chest, measured: its owner gets 104 open and 105 lock ("entrar en mi casa", frame
        /// 16); anybody else 104 alone (the guild house's chests, "Gremio/pocima hogar a gremio",
        /// frame 49). A chest of a house nobody owns is nobody's to use: nothing.
        /// </remarks>
        public static IReadOnlyList<InteractiveAction> VisibleActions(RegisteredInteractive interactive, long viewerAccountId)
        {
            bool door = false, chest = false;
            foreach (var action in interactive.Actions)
            {
                if (action.Kind == InteractiveActionKind.HouseDoor) door = true;
                else if (action.Kind == InteractiveActionKind.HouseChest) chest = true;
            }
            if (!door && !chest) return interactive.Actions;

            var visible = new List<InteractiveAction>();
            if (door)
            {
                if (!Houses.TryGetDoor(interactive.MapId, interactive.Element.Id, out var houseDoor) || !houseDoor.IsOwnable)
                    return interactive.Actions;

                var house = HouseStore.Of(interactive.MapId, interactive.Element.Id);
                bool mine = house.Owned && house.AccountId == viewerAccountId;
                foreach (int skill in DoorSkillsFor(house, mine))
                {
                    var action = Find(interactive, skill);
                    if (action != null) visible.Add(action);
                }
                return visible;
            }

            var (doorMap, doorElement) = CurrentHouse();
            if (doorMap == 0) return visible;
            var owner = HouseStore.Of(doorMap, doorElement);
            if (!owner.Owned) return visible;
            foreach (var action in interactive.Actions)
            {
                if (action.SkillId == Houses.ChestOpenSkill
                    || (action.SkillId == Houses.ChestLockSkill && owner.AccountId == viewerAccountId))
                    visible.Add(action);
            }
            return visible;
        }

        /// <summary>The door skills for its owner or for anybody else, in the order the jss declares them.</summary>
        public static int[] DoorSkillsFor(HouseStore.House house, bool mine)
        {
            if (mine) return new[] { Houses.EnterSkill, Houses.CodeSkill, house.ForSale ? Houses.PriceSkill : Houses.SellSkill };
            bool buyable = !house.Owned || house.ForSale;
            return buyable ? new[] { Houses.EnterSkill, Houses.BuySkill } : new[] { Houses.EnterSkill };
        }

        private static InteractiveAction? Find(RegisteredInteractive interactive, int skill)
        {
            foreach (var action in interactive.Actions)
            {
                if (action.SkillId == skill) return action;
            }
            return null;
        }

        /// <summary>Whether this session's account owns the house. Nobody owns a house nobody owns.</summary>
        private static bool IsOwner(HouseStore.House house)
            => house.Owned && house.AccountId == SessionContext.Current.AccountId;

        private static bool Contains(IReadOnlyList<InteractiveAction> actions, InteractiveAction action)
        {
            foreach (var candidate in actions)
            {
                if (ReferenceEquals(candidate, action)) return true;
            }
            return false;
        }

        /// <summary>
        /// The house this character is inside: the door he came in by, or the one the data gives
        /// for the interior when he reconnected inside. (0, 0) when he is not in a house.
        /// </summary>
        public static (long MapId, int ElementId) CurrentHouse()
        {
            long here = SessionContext.State.MapId;
            if (!Houses.IsInterior(here)) return (0, 0);

            long map = SessionContext.State.HouseEntryMapId;
            int element = SessionContext.State.HouseEntryElementId;
            if (map != 0 && element != 0 && Houses.TryGetDoor(map, element, out var door) && door.InteriorMapId == here)
                return (map, element);

            return Houses.TryGetWayBack(here, out var wayBack) ? (wayBack.MapId, wayBack.ElementId) : (0, 0);
        }

        // ─── The plaque ─────────────────────────────────────────────────────────

        /// <summary>What the plaque of this house says now.</summary>
        public static HouseProtocol.Plaque PlaqueOf(HouseStore.House house, Houses.Door door)
        {
            string name = "", tag = "";
            if (house.Owned) (name, tag) = AccountTagOf(house.AccountId);
            return new HouseProtocol.Plaque
            {
                Instance = Houses.Instance,
                OwnerName = name,
                OwnerTag = tag,
                Price = house.ForSale ? house.Price : 0,
                Locked = house.Owned && house.Locked,
                Rooms = door.Rooms,
            };
        }

        /// <summary>
        /// An account as the plaque names it: its nickname and the tag next to it, the same pair the
        /// authentication gives the account's own client.
        /// </summary>
        public static (string Name, string Tag) AccountTagOf(long accountId)
            => (DatabaseManager.GetAccountById(accountId)?.Nickname ?? "Jondo",
                GameServerProxy.BuildAccountTag(accountId));

        /// <summary>
        /// The houses of a map's jss: f7, the one the viewer is inside, and f9, those on this
        /// street that have an owner. A house nobody owns sends nothing, as before: no plaque of a
        /// free house was ever captured.
        /// </summary>
        public static void AddToMap(Pb jss, long mapId)
        {
            if (Houses.IsInterior(mapId))
            {
                var (doorMap, doorElement) = CurrentHouse();
                if (doorMap != 0 && Houses.TryGetDoor(doorMap, doorElement, out var door) && door.IsOwnable)
                {
                    var house = HouseStore.Of(doorMap, doorElement);
                    var street = MapManager.GetMapInfo(doorMap);
                    if (house.Owned)
                    {
                        jss.Msg(7, HouseProtocol.BuildInterior(Houses.HouseIdOf(doorMap, doorElement), door.Model,
                            street?.PosX ?? 0, street?.PosY ?? 0, PlaqueOf(house, door)));
                    }
                }
            }

            foreach (var door in Houses.On(mapId))
            {
                if (!door.IsOwnable) continue;
                var house = HouseStore.Of(mapId, door.ElementId);
                if (!house.Owned) continue;
                jss.Msg(9, HouseProtocol.BuildOnMap(Houses.HouseIdOf(mapId, door.ElementId), door.Model,
                    new long[] { door.ElementId }, new[] { PlaqueOf(house, door) }));
            }
        }

        /// <summary>
        /// izv { f1: instance, f2: house }: the plaque, answered with izr { f1: full plaque } on
        /// the request's id (sale frames 4-5). False when it names no house.
        /// </summary>
        public static async Task<bool> InfoAsync(NetworkStream? stream, byte[] payload)
        {
            byte[]? izv = ConnectionProtocol.ReadPayload(payload, Op.Izv);
            if (izv == null) return false;

            int houseId = 0;
            foreach (var field in ProtoMessage.Parse(izv).Fields)
            {
                if (field.FieldNumber == 2 && field.WireType == 0) houseId = (int)field.VarIntValue;
            }
            if (!Houses.TryGetByHouseId(houseId, out var door)) return false;

            var house = HouseStore.Of(door.MapId, door.ElementId);
            byte[] answer = ConnectionProtocol.Answer(Op.Izr, HouseProtocol.BuildInfo(PlaqueOf(house, door)),
                                                      ConnectionProtocol.RequestId(payload));
            await WriteAsync(stream, answer);
            return true;
        }

        /// <summary>
        /// The account's houses (jaa), at the world entry. Empty for an account with none, which is
        /// what the character-creation captures send.
        /// </summary>
        public static Task SendAccountHousesAsync(NetworkStream? stream)
            => SendAsync(stream, Op.Jaa, BuildAccountHouses(SessionContext.Current.AccountId));

        public static byte[] BuildAccountHouses(long accountId)
        {
            var owned = new List<HouseProtocol.Owned>();
            foreach (var house in HouseStore.OwnedBy(accountId))
            {
                if (!Houses.TryGetDoor(house.MapId, house.ElementId, out var door)) continue;
                var street = MapManager.GetMapInfo(house.MapId);
                owned.Add(new HouseProtocol.Owned
                {
                    HouseId = Houses.HouseIdOf(house.MapId, house.ElementId),
                    Model = door.Model,
                    StreetMapId = house.MapId,
                    WorldX = street?.PosX ?? 0,
                    WorldY = street?.PosY ?? 0,
                    SubAreaId = street?.SubAreaId ?? 0,
                    Plaque = PlaqueOf(house, door),
                });
            }
            return HouseProtocol.BuildAccountHouses(owned);
        }

        // ─── Selling ────────────────────────────────────────────────────────────

        /// <summary>
        /// The owner's sale window: khr with the price the house is at, or its model's when it is
        /// not on sale (sale frame 3: 2,500,000, the Casa grande de Bonta's).
        /// </summary>
        private static async Task OpenSaleAsync(NetworkStream? stream, int elementId, int skillId)
        {
            long map = SessionContext.State.MapId;
            if (!Houses.TryGetDoor(map, elementId, out var door)) return;
            var house = HouseStore.Of(map, elementId);
            if (!IsOwner(house)) return;

            await SendAsync(stream, Op.Iwn, ConnectionProtocol.BuildElementInUse(elementId, skillId, SessionContext.State.CharacterId));

            long price = house.ForSale ? house.Price : door.Price;
            SessionContext.State.HouseDialog = new Dialog
            {
                Kind = DialogKind.Sale, DoorMapId = map, DoorElementId = elementId, Price = price,
            };
            await SendAsync(stream, Op.Khr, HouseProtocol.BuildSaleWindow(false, Houses.HouseIdOf(map, elementId),
                                                                          Houses.Instance, price));
        }

        /// <summary>
        /// jan { f1: price, f2: instance, f3: on sale }: the sale window's answer. On sale at that
        /// price, or off sale without f3; then the burst of the sale's frames 13-21 (see the class).
        /// False when no sale window is open.
        /// </summary>
        public static async Task<bool> SellAsync(NetworkStream? stream, byte[] payload)
        {
            var dialog = OpenDialog(DialogKind.Sale);
            if (dialog == null || dialog.Answered) return false;

            byte[]? jan = ConnectionProtocol.ReadPayload(payload, Op.Jan);
            if (jan == null) return true;

            long price = 0;
            bool onSale = false;
            foreach (var field in ProtoMessage.Parse(jan).Fields)
            {
                if (field.WireType != 0) continue;
                if (field.FieldNumber == 1) price = field.VarIntValue;
                else if (field.FieldNumber == 3) onSale = field.VarIntValue != 0;
            }
            dialog.Answered = true;

            if (!Houses.TryGetDoor(dialog.DoorMapId, dialog.DoorElementId, out var door)) return true;
            long account = SessionContext.Current.AccountId;
            long newPrice = onSale && price > 0 ? price : 0;
            if (!await HouseStore.SetPriceAsync(dialog.DoorMapId, dialog.DoorElementId, account, newPrice))
            {
                await SendAsync(stream, Op.Kld, ConnectionProtocol.BuildDialogClosed(HouseProtocol.SaleClosed));
                return true;
            }

            int houseId = Houses.HouseIdOf(dialog.DoorMapId, dialog.DoorElementId);
            var house = HouseStore.Of(dialog.DoorMapId, dialog.DoorElementId);
            var offSale = new HouseStore.House
            {
                MapId = house.MapId, ElementId = house.ElementId, AccountId = house.AccountId,
                Price = 0, AccessCode = house.AccessCode,
            };

            // First the house off sale, whatever comes next; then, if it goes on sale, jjt and the
            // house at its price. That is the order of the sale's frames 13-17.
            await SendDoorAndPlaqueAsync(stream, door, offSale, account);
            if (house.ForSale)
            {
                await SendAsync(stream, Op.Jjt, HouseProtocol.BuildPutOnSale(Houses.Instance, houseId));
                await SendDoorAndPlaqueAsync(stream, door, house, account);
            }

            var (name, tag) = AccountTagOf(account);
            await SendAsync(stream, Op.Jaa, BuildAccountHouses(account));
            await SendAsync(stream, Op.Izu, HouseProtocol.BuildSellingUpdate(name, tag, houseId, house.ForSale ? house.Price : 0, Houses.Instance));
            await SendAsync(stream, Op.Izz, HouseProtocol.BuildChanged(houseId, new long[] { door.ElementId }, PlaqueOf(house, door)));
            await SendAsync(stream, Op.Kld, ConnectionProtocol.BuildDialogClosed(HouseProtocol.SaleClosed));

            await TellTheStreetAsync(door, house);
            Console.WriteLine(house.ForSale
                ? $"[Houses] {door.MapId}/{door.ElementId} on sale for {house.Price:N0} kamas."
                : $"[Houses] {door.MapId}/{door.ElementId} taken off sale.");
            return true;
        }

        /// <summary>The door as this owner sees it (iwm) and the house's plaque (izz).</summary>
        private static async Task SendDoorAndPlaqueAsync(NetworkStream? stream, Houses.Door door, HouseStore.House house, long viewer)
        {
            await SendAsync(stream, Op.Iwm, DoorChanged(door, house, viewer));
            await SendAsync(stream, Op.Izz, HouseProtocol.BuildChanged(Houses.HouseIdOf(door.MapId, door.ElementId),
                new long[] { door.ElementId }, PlaqueOf(house, door)));
        }

        /// <summary>The door redeclared with the skills this viewer has now (iwm).</summary>
        public static byte[] DoorChanged(Houses.Door door, HouseStore.House house, long viewerAccountId)
        {
            var skills = new List<(int, int)>();
            bool mine = house.Owned && house.AccountId == viewerAccountId;
            foreach (int skill in DoorSkillsFor(house, mine)) skills.Add((DoorSkillInstance(door.ElementId, skill), skill));
            return HouseProtocol.BuildElementChanged(door.ElementId, Houses.DoorType, skills);
        }

        /// <summary>
        /// The skill instance of each door skill, the one the registry declares. Enter keeps the
        /// historical one; the others go a million apart per skill, so no two elements of a map
        /// can share one.
        /// </summary>
        public static int DoorSkillInstance(int elementId, int skill)
        {
            int slot = skill switch
            {
                Houses.BuySkill => 1,
                Houses.SellSkill => 2,
                Houses.CodeSkill => 3,
                Houses.PriceSkill => 4,
                Houses.ChestLockSkill => 1,
                _ => 0,
            };
            return slot * 1_000_000 + Interactives.SkillInstanceOf(elementId);
        }

        /// <summary>
        /// Everybody else on the street gets the door as they see it and the plaque. Nobody else
        /// was there in the captures: an inference.
        /// </summary>
        private static async Task TellTheStreetAsync(Houses.Door door, HouseStore.House house)
        {
            byte[] plaque = ConnectionProtocol.Push(Op.Izz, HouseProtocol.BuildChanged(
                Houses.HouseIdOf(door.MapId, door.ElementId), new long[] { door.ElementId }, PlaqueOf(house, door)));
            foreach (var session in SessionRegistry.OnMap(door.MapId))
            {
                if (session.Id == SessionContext.Current.Id) continue;
                try
                {
                    await session.SendAsync(ConnectionProtocol.Push(Op.Iwm, DoorChanged(door, house, session.AccountId))).ConfigureAwait(false);
                    await session.SendAsync(plaque).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Houses] {session.CharacterId} could not be told of house {door.MapId}/{door.ElementId}: {ex.Message}");
                }
            }
        }

        // ─── Buying ─────────────────────────────────────────────────────────────

        /// <summary>
        /// "Comprar" on the door of a house on sale, or of one nobody owns: the sale window, as a
        /// buyer's. INFERENCE: khr with its f1 set (see <see cref="HouseProtocol.BuildSaleWindow"/>).
        /// </summary>
        private static async Task OpenPurchaseAsync(NetworkStream? stream, int elementId, int skillId)
        {
            long map = SessionContext.State.MapId;
            if (!Houses.TryGetDoor(map, elementId, out var door) || !door.IsOwnable) return;
            var house = HouseStore.Of(map, elementId);
            long price = HouseStore.AskingPrice(house, door.Price);
            if (price <= 0 || house.AccountId == SessionContext.Current.AccountId) return;

            await SendAsync(stream, Op.Iwn, ConnectionProtocol.BuildElementInUse(elementId, skillId, SessionContext.State.CharacterId));

            SessionContext.State.HouseDialog = new Dialog
            {
                Kind = DialogKind.Purchase, DoorMapId = map, DoorElementId = elementId, Price = price,
            };
            await SendAsync(stream, Op.Khr, HouseProtocol.BuildSaleWindow(true, Houses.HouseIdOf(map, elementId),
                                                                          Houses.Instance, price));
        }

        /// <summary>
        /// The buyer's yes. INFERENCE: no capture buys a house, so the client's message for it is
        /// not measured. The client's code index (datos/indice_3.6.10.10.json) has the house's
        /// requests built by one class -- jan the sale, khv the code, and jad and jal, each a lone
        /// int64 -- and a purchase says a price and nothing else. Either of the two is taken as the
        /// yes, but ONLY with a buyer's window open and only when its number is the price that
        /// window showed: whatever the other one is, it cannot buy a house by accident.
        /// </summary>
        public static async Task<bool> BuyAsync(NetworkStream? stream, byte[] payload, string opcode)
        {
            var dialog = OpenDialog(DialogKind.Purchase);
            if (dialog == null || dialog.Answered) return false;

            byte[]? body = ConnectionProtocol.ReadPayload(payload, opcode);
            if (body == null) return false;
            long offered = 0;
            foreach (var field in ProtoMessage.Parse(body).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 0) offered = field.VarIntValue;
            }
            if (offered != dialog.Price) return false;

            dialog.Answered = true;
            if (!Houses.TryGetDoor(dialog.DoorMapId, dialog.DoorElementId, out var door)) return true;

            var state = SessionContext.State;
            long buyer = SessionContext.Current.AccountId;
            var (done, why) = await HouseStore.BuyAsync(dialog.DoorMapId, dialog.DoorElementId, door.Price,
                                                        buyer, state.CharacterId, state.Kamas, dialog.Price);
            if (done == null)
            {
                int? message = why switch
                {
                    HouseStore.Refusal.NotEnoughKamas => NotEnoughKamas,
                    HouseStore.Refusal.SellerBankFull => SellerBankFull,
                    _ => null,
                };
                if (message.HasValue)
                {
                    int type = why == HouseStore.Refusal.SellerBankFull ? InfoMessages.Info : InfoMessages.Warning;
                    await SendAsync(stream, Op.Lqn, ConnectionProtocol.BuildInfoMessage(type, message.Value));
                }
                await SendAsync(stream, Op.Kld, ConnectionProtocol.BuildDialogClosed(HouseProtocol.SaleClosed));
                Console.WriteLine($"[Houses] {state.CharacterId} could not buy {door.MapId}/{door.ElementId}: {why}.");
                return true;
            }

            state.Kamas = done.BuyerKamas;
            await SendAsync(stream, Op.Ivf, ConnectionProtocol.BuildKamas(done.BuyerKamas));

            var house = HouseStore.Of(door.MapId, door.ElementId);
            await SendDoorAndPlaqueAsync(stream, door, house, buyer);
            await SendAsync(stream, Op.Jaa, BuildAccountHouses(buyer));
            await SendAsync(stream, Op.Kld, ConnectionProtocol.BuildDialogClosed(HouseProtocol.SaleClosed));

            await TellTheStreetAsync(door, house);
            if (done.SellerAccountId > 0) await TellTheSellerAsync(done, state.CharacterName);

            Console.WriteLine($"[Houses] {door.MapId}/{door.ElementId} bought by account {buyer} for {done.Price:N0} kamas.");
            return true;
        }

        /// <summary>1/82 "No tienes suficientes kamas para poder realizar esta acción."</summary>
        public const int NotEnoughKamas = 82;

        /// <summary>0/342 "No puedes comprar esta casa porque la cuenta del banco del vendedor está repleta."</summary>
        public const int SellerBankFull = 342;

        /// <summary>4/5 "{1} acaba de comprar una de tus casas por $quantity{0} kamas..."</summary>
        public const int HouseBoughtType = 4;
        public const int HouseBought = 5;

        /// <summary>1/264 "Se ha(n) transferido {0} lote(s) de objetos a tu banco desde los cofres de tu casa."</summary>
        public const int ChestLotsToBank = 264;

        /// <summary>
        /// The seller, wherever he is connected: the client's own sentences for a house bought and
        /// for what its chests handed over, and his list of houses again. Inference: see
        /// <see cref="HouseStore.BuyAsync"/>.
        /// </summary>
        private static async Task TellTheSellerAsync(HouseStore.Purchase done, string buyerName)
        {
            byte[] houses = ConnectionProtocol.Push(Op.Jaa, BuildAccountHouses(done.SellerAccountId));
            byte[] bought = ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(HouseBoughtType, HouseBought,
                done.Price.ToString(CultureInfo.InvariantCulture), buyerName ?? ""));
            byte[]? lots = done.LotsToSeller > 0
                ? ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, ChestLotsToBank,
                    done.LotsToSeller.ToString(CultureInfo.InvariantCulture)))
                : null;

            foreach (var session in SessionRegistry.InWorld())
            {
                if (session.AccountId != done.SellerAccountId) continue;
                try
                {
                    await session.SendAsync(bought).ConfigureAwait(false);
                    if (lots != null) await session.SendAsync(lots).ConfigureAwait(false);
                    await session.SendAsync(houses).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Houses] The seller {session.CharacterId} could not be told: {ex.Message}");
                }
            }
        }

        // ─── Codes ──────────────────────────────────────────────────────────────

        /// <summary>"Modificar el código" on one's own door: the owner's keypad, kia { f3: 8 }.</summary>
        private static async Task OpenDoorKeypadAsync(NetworkStream? stream, int elementId, int skillId)
        {
            long map = SessionContext.State.MapId;
            var house = HouseStore.Of(map, elementId);
            if (!IsOwner(house)) return;

            await SendAsync(stream, Op.Iwn, ConnectionProtocol.BuildElementInUse(elementId, skillId, SessionContext.State.CharacterId));
            SessionContext.State.HouseDialog = new Dialog { Kind = DialogKind.DoorCode, DoorMapId = map, DoorElementId = elementId };
            await SendAsync(stream, Op.Kia, HouseProtocol.BuildCodeKeypad(toGetIn: false));
        }

        /// <summary>
        /// khv { f1: code }: the owner's keypad answered. The door's: iwm, izz, khu, kld 2; a
        /// chest's: khu, kld 2. No f1 -- Accept with nothing typed -- takes the code off. False
        /// when no owner's keypad is open.
        /// </summary>
        public static async Task<bool> ChangeCodeAsync(NetworkStream? stream, byte[] payload)
        {
            var dialog = OpenDialog(DialogKind.DoorCode, DialogKind.ChestCode);
            if (dialog == null || dialog.Answered) return false;

            byte[]? khv = ConnectionProtocol.ReadPayload(payload, Op.Khv);
            string code = khv == null ? "" : TextOf(khv);
            dialog.Answered = true;
            SessionContext.State.HouseDialog = null;

            long account = SessionContext.Current.AccountId;
            if (dialog.Kind == DialogKind.DoorCode)
            {
                if (await HouseStore.SetAccessCodeAsync(dialog.DoorMapId, dialog.DoorElementId, account, code)
                    && Houses.TryGetDoor(dialog.DoorMapId, dialog.DoorElementId, out var door))
                {
                    var house = HouseStore.Of(dialog.DoorMapId, dialog.DoorElementId);
                    await SendDoorAndPlaqueAsync(stream, door, house, account);
                    await SendAsync(stream, Op.Khu, HouseProtocol.BuildCodeResult(wrong: false));
                    await TellTheStreetAsync(door, house);
                    Console.WriteLine($"[Houses] {dialog.DoorMapId}/{dialog.DoorElementId}: access code {(code.Length == 0 ? "removed" : "set")}.");
                }
            }
            else if (await HouseStore.SetChestCodeAsync(dialog.DoorMapId, dialog.DoorElementId, dialog.ChestElementId, account, code))
            {
                await SendAsync(stream, Op.Khu, HouseProtocol.BuildCodeResult(wrong: false));
                Console.WriteLine($"[Houses] Chest {dialog.ChestElementId} of {dialog.DoorMapId}/{dialog.DoorElementId}: " +
                                  $"code {(code.Length == 0 ? "removed" : "set")}.");
            }

            await SendAsync(stream, Op.Kld, ConnectionProtocol.BuildDialogClosed(HouseProtocol.CodeClosed));
            return true;
        }

        /// <summary>
        /// khw { f1: code }: a stranger's keypad answered. Wrong: kld 2 and khu { f2: 1 }, pandala
        /// frames 3889-3890. Right: the same kld and an empty khu, then in -- or the chest opens.
        /// That second half is an inference: nobody typed the right code of somebody else's house
        /// in the captures. False when no stranger's keypad is open.
        /// </summary>
        public static async Task<bool> UseCodeAsync(NetworkStream? stream, byte[] payload)
        {
            var dialog = OpenDialog(DialogKind.EnterCode, DialogKind.ChestKeypad);
            if (dialog == null || dialog.Answered) return false;

            byte[]? khw = ConnectionProtocol.ReadPayload(payload, Op.Khw);
            string typed = khw == null ? "" : TextOf(khw);
            dialog.Answered = true;
            SessionContext.State.HouseDialog = null;

            string expected = dialog.Kind == DialogKind.EnterCode
                ? HouseStore.Of(dialog.DoorMapId, dialog.DoorElementId).AccessCode
                : HouseStore.ChestCodeOf(dialog.DoorMapId, dialog.DoorElementId, dialog.ChestElementId);
            bool right = HouseStore.Matches(expected, typed);

            await SendAsync(stream, Op.Kld, ConnectionProtocol.BuildDialogClosed(HouseProtocol.CodeClosed));
            await SendAsync(stream, Op.Khu, HouseProtocol.BuildCodeResult(wrong: !right));
            if (!right)
            {
                Console.WriteLine($"[Houses] {SessionContext.State.CharacterId} typed a wrong code at {dialog.DoorMapId}/{dialog.DoorElementId}.");
                return true;
            }

            if (dialog.Kind == DialogKind.EnterCode)
            {
                if (SessionContext.State.MapId == dialog.DoorMapId && Houses.TryGetDoor(dialog.DoorMapId, dialog.DoorElementId, out var door))
                    await GoInsideAsync(stream, door);
            }
            else
            {
                await OpenChestAsync(stream, dialog.DoorMapId, dialog.DoorElementId, dialog.ChestElementId);
            }
            return true;
        }

        private static string TextOf(byte[] body)
        {
            foreach (var field in ProtoMessage.Parse(body).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 2) return System.Text.Encoding.UTF8.GetString(field.BytesValue);
            }
            return "";
        }

        // ─── The chest ──────────────────────────────────────────────────────────

        /// <summary>A house chest was used: 104 opens it, 105 is the owner's keypad for its lock.</summary>
        public static async Task UseChestAsync(NetworkStream? stream, RegisteredInteractive chest, InteractiveAction action)
        {
            if (!Contains(VisibleActions(chest, SessionContext.Current.AccountId), action))
            {
                Console.WriteLine($"[Houses] Skill {action.SkillId} of chest {chest.Element.Id} is not offered to this character.");
                return;
            }

            var (doorMap, doorElement) = CurrentHouse();
            if (doorMap == 0) return;
            var house = HouseStore.Of(doorMap, doorElement);
            if (!house.Owned) return;
            bool mine = IsOwner(house);

            await SendAsync(stream, Op.Iwn, ConnectionProtocol.BuildElementInUse(
                chest.Element.Id, action.SkillId, SessionContext.State.CharacterId));

            if (action.SkillId == Houses.ChestLockSkill)
            {
                SessionContext.State.HouseDialog = new Dialog
                {
                    Kind = DialogKind.ChestCode, DoorMapId = doorMap, DoorElementId = doorElement, ChestElementId = chest.Element.Id,
                };
                await SendAsync(stream, Op.Kia, HouseProtocol.BuildCodeKeypad(toGetIn: false));
                return;
            }

            // A locked chest asks a stranger for its code, as a locked door does: an inference.
            if (!mine && HouseStore.ChestCodeOf(doorMap, doorElement, chest.Element.Id).Length > 0)
            {
                SessionContext.State.HouseDialog = new Dialog
                {
                    Kind = DialogKind.ChestKeypad, DoorMapId = doorMap, DoorElementId = doorElement, ChestElementId = chest.Element.Id,
                };
                await SendAsync(stream, Op.Kia, HouseProtocol.BuildCodeKeypad(toGetIn: true));
                return;
            }

            await OpenChestAsync(stream, doorMap, doorElement, chest.Element.Id);
        }

        /// <summary>kci { 100, 4 } and what is inside: frames 8-9 of the house chest capture.</summary>
        private static Task OpenChestAsync(NetworkStream? stream, long doorMap, int doorElement, int chestElement)
            => StorageHandler.OpenAsync(stream, new StorageHandler.Window
            {
                Kind = StorageHandler.Kind.HouseChest,
                Place = StorageStacks.HouseChest(doorMap, doorElement, chestElement),
                MapId = SessionContext.State.MapId,
                ElementId = chestElement,
            }, StorageProtocol.BuildHouseChestOpened());

        /// <summary>1/600 "No se pueden depositar kamas en un cofre de casa."</summary>
        public const int NoKamasInHouseChest = 600;

        /// <summary>
        /// kee with a house chest open: refused with the client's own sentence for it. An
        /// inference about when it is said. False when no house chest is open.
        /// </summary>
        public static async Task<bool> KamasAsync(NetworkStream? stream)
        {
            var window = StorageHandler.Current;
            if (window == null || window.Kind != StorageHandler.Kind.HouseChest) return false;
            await SendAsync(stream, Op.Lqn, ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, NoKamasInHouseChest));
            return true;
        }

        // ─── Closing ────────────────────────────────────────────────────────────

        /// <summary>
        /// kla with a house window open. The sale's kla comes right behind its jan (frames 11-12)
        /// and the burst already closes the window, so an answered one closes silently; one closed
        /// without an answer gets its kld (3 the sale's, 2 a keypad's: an inference for a window
        /// nobody closed in the captures). False when none is open.
        /// </summary>
        public static async Task<bool> CloseDialogAsync(NetworkStream? stream)
        {
            var dialog = OpenDialog();
            if (dialog == null) return false;

            SessionContext.State.HouseDialog = null;
            if (!dialog.Answered)
            {
                int reason = dialog.Kind == DialogKind.Sale || dialog.Kind == DialogKind.Purchase
                    ? HouseProtocol.SaleClosed
                    : HouseProtocol.CodeClosed;
                await SendAsync(stream, Op.Kld, ConnectionProtocol.BuildDialogClosed(reason));
            }
            return true;
        }

        // ─── Wire ───────────────────────────────────────────────────────────────

        private static Task SendAsync(NetworkStream? stream, string opcode, byte[] body)
            => StorageHandler.SendAsync(stream, opcode, body);

        private static Task WriteAsync(NetworkStream? stream, byte[] frame)
            => stream != null
                ? Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame)
                : SessionContext.Current.SendAsync(frame);
    }
}
