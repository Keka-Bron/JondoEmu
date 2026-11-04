using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Going in and out of a house.
    ///
    /// The two halves are different on the wire, and that was the surprise. Measured from the
    /// captures «entrar en mi casa» and «desde dentro de casa salir a fuera»:
    ///
    ///   going in   iwo { f1: skill, f2: element, f3: instance }
    ///              iwn { f1: 1, f2: element, f4: 84, f5: character }
    ///              jqw { f1: interior map }
    ///
    ///   going out  iwo { f1: skill, f2: element }
    ///              iwn { f1: 1, f2: element, f4: 184, f5: character }
    ///              jru { f2: street map }
    ///
    /// The map travels in field 1 of the jqw and in field 2 of the jru. Sending a jru to go in would
    /// make the client load the map without knowing it is entering a dwelling.
    ///
    /// After either of the two the client asks for the map by itself with kmv and jrh, so the jss is
    /// not sent here: the notice is sent and it is left to ask, which is what the real server does.
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

            /// <summary>The house: its street door, and which copy of the building.</summary>
            public long DoorMapId { get; init; }
            public int DoorElementId { get; init; }
            public int Instance { get; init; } = Houses.FirstInstance;

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

        /// <summary>
        /// A house door was used: enter, buy, sell or the code, by the skill, on the copy of the
        /// building the iwo's f3 names -- the entry of the client's door menu. A building of one copy
        /// may not name it: its first.
        /// </summary>
        public static async Task UseDoorAsync(NetworkStream? stream, RegisteredInteractive door, InteractiveAction action, int instance)
        {
            if (!Contains(VisibleActions(door, SessionContext.Current.AccountId), action))
            {
                Console.WriteLine($"[Houses] Skill {action.SkillId} of door {door.Element.Id} is not offered to this character.");
                return;
            }
            if (instance == 0) instance = Houses.FirstInstance;

            switch (action.SkillId)
            {
                case Houses.EnterSkill:
                    await EnterAsync(stream, door.Element.Id, action.SkillId, instance);
                    break;
                case Houses.BuySkill:
                    await OpenPurchaseAsync(stream, door.Element.Id, action.SkillId, instance);
                    break;
                case Houses.SellSkill:
                case Houses.PriceSkill:
                    await OpenSaleAsync(stream, door.Element.Id, action.SkillId, instance);
                    break;
                case Houses.CodeSkill:
                    await OpenDoorKeypadAsync(stream, door.Element.Id, action.SkillId, instance);
                    break;
            }
        }

        /// <summary>
        /// «Ir» from a list of houses (jab: f1 the house, f2 its copy): the house window's list of
        /// one's own and the guild's «Casas» tab, which enables it for one's own and for the houses
        /// whose rights let the guild teleport (bit 128). Inside, as the home potion takes one.
        /// </summary>
        public static async Task GoToHouseAsync(NetworkStream? stream, byte[] frame)
        {
            byte[] jab = ConnectionProtocol.ReadPayload(frame, Op.Jab);
            if (jab == null) return;
            int houseId = 0, instance = 0;
            foreach (var field in ProtoMessage.Parse(jab).Fields)
            {
                if (field.WireType != 0) continue;
                if (field.FieldNumber == 1) houseId = (int)field.VarIntValue;
                if (field.FieldNumber == 2) instance = (int)field.VarIntValue;
            }
            if (instance == 0) instance = Houses.FirstInstance;

            if (!Houses.TryGetByHouseId(houseId, out var door) || !door.HasCopy(instance)
                || door.InteriorMapId == 0 || MapManager.GetMapInfo(door.InteriorMapId) == null)
            {
                Console.WriteLine($"[Houses] No house {houseId}, copy {instance}, to go to.");
                return;
            }
            var house = HouseStore.Of(door.MapId, door.ElementId, instance);
            if (!MayTeleportTo(house, SessionContext.Current.AccountId, MyGuildId()))
            {
                Console.WriteLine($"[Houses] {SessionContext.State.CharacterName} may not go to house {houseId}, copy {instance}.");
                return;
            }
            if (await Managers.Jail.KeepsInAsync(stream)) return;
            if (await Managers.HeadHunts.HoldsAsync(stream, Managers.HeadHunts.Hold.House)) return;
            if (stream != null && await ConquestHandler.HoldsInAsync(stream, ConquestHandler.NoTeleportInAWar)) return;
            await GoHomeAsync(stream, door, instance);
        }

        /// <summary>Whether one may go to a house from a list: one's own, or one's guild's that shares the teleport.</summary>
        public static bool MayTeleportTo(HouseStore.House house, long accountId, long guildId)
            => house != null && house.Owned
               && (house.AccountId == accountId
                   || InTheGuild(house, guildId) && (house.GuildRights & HouseStore.GuildRights.Teleport) != 0);

        /// <summary>The client has clicked the street door, to go into this copy of the building.</summary>
        public static async Task EnterAsync(NetworkStream? stream, int elementId, int skillId, int instance)
        {
            if (await Managers.Jail.KeepsInAsync(stream)) return;
            if (await Managers.HeadHunts.HoldsAsync(stream, Managers.HeadHunts.Hold.House)) return;
            long here = SessionContext.State.MapId;
            if (!Houses.TryGetDoor(here, elementId, out var door) || !door.HasCopy(instance))
            {
                Console.WriteLine($"[Houses] Unknown door: map {here}, element {elementId}, copy {instance}.");
                return;
            }

            await SendAsync(stream, Op.Iwn, ConnectionProtocol.BuildElementInUse(
                elementId, skillId, SessionContext.State.CharacterId));

            // A house that is not one's own asks for its code first (pandala, frame 3884), unless
            // it is shared with one's guild and the guild may come in without it.
            var house = HouseStore.Of(here, elementId, instance);
            if (EnterAccess(house, SessionContext.Current.AccountId, MyGuildId()) == Access.Keypad)
            {
                SessionContext.State.HouseDialog = new Dialog
                {
                    Kind = DialogKind.EnterCode, DoorMapId = here, DoorElementId = elementId, Instance = instance,
                };
                await SendAsync(stream, Op.Kia, HouseProtocol.BuildCodeKeypad(toGetIn: true));
                return;
            }

            await GoInsideAsync(stream, door, instance);
        }

        /// <summary>Through the door, once it is open: the part of entering after the iwn.</summary>
        private static Task GoInsideAsync(NetworkStream? stream, Houses.Door door, int instance)
            => GoInsideAsync(stream, door, instance, SessionContext.State.MapId, SessionContext.State.CellId);

        /// <summary>
        /// Into one's own house from wherever one is, as the home potion takes him: the capture
        /// «utilizar pocima de hogar» goes in with the same jqw as the door. Leaving goes out
        /// through the street door, not back to where the potion was drunk.
        /// </summary>
        internal static Task GoHomeAsync(NetworkStream? stream, Houses.Door door, int instance)
            => GoInsideAsync(stream, door, instance, door.MapId, door.Cell);

        private static async Task GoInsideAsync(NetworkStream? stream, Houses.Door door, int instance, long wayBackMap, int wayBackCell)
        {
            long mapaQueDeja = SessionContext.State.MapId;
            SessionContext.State.HouseEntryMapId = wayBackMap;
            SessionContext.State.HouseEntryCell = wayBackCell;
            SessionContext.State.HouseEntryElementId = door.ElementId;
            SessionContext.State.HouseEntryInstance = instance;
            SessionContext.State.MapId = door.InteriorMapId;

            // He comes in next to the exit, not on it: that way the first click to leave falls nearby.
            int destino = Houses.TryGetExit(door.InteriorMapId, out var exit) ? exit.Cell : 0;
            SessionContext.State.CellId = MapManager.GetNearestWalkableCell(door.InteriorMapId, destino);
            DatabaseManager.SaveCurrentCharacter();

            await SessionRegistry.AnunciarMudanzaAsync(SessionContext.Current, mapaQueDeja);

            await WriteAsync(stream, ConnectionProtocol.BuildActorLeft(SessionContext.State.CharacterId));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jqw, Pb.New().Var(1, door.InteriorMapId).Build()));
            await WriteAsync(stream, ConnectionProtocol.BuildMapClock());

            string cual = door.IsKnown
                ? $"«{door.Name}», copy {instance} of {door.Copies}"
                : $"door {door.ElementId}";
            Console.WriteLine($"[Houses] Going in through {cual} from map {mapaQueDeja} to the interior " +
                              $"{door.InteriorMapId}, cell {SessionContext.State.CellId}.");
        }

        // ─── Who may come in ────────────────────────────────────────────────────

        /// <summary>How somebody comes into a house: straight in, or asked its code.</summary>
        public enum Access { In, Keypad }

        /// <summary>
        /// Whether one comes straight into a house or is asked its code. Its owner comes in, and
        /// anybody into a house nobody owns -- it is on sale, to be seen. Into anybody else's only
        /// with its code, or its guild with the right to come in without it. A house without a code
        /// lets nobody else in: the code is how an owner opens it to others (his wish, 21/10/2026).
        /// </summary>
        public static Access EnterAccess(HouseStore.House house, long accountId, long guildId)
        {
            if (!house.Owned || house.AccountId == accountId) return Access.In;
            if (InTheGuild(house, guildId) && (house.GuildRights & HouseStore.GuildRights.HouseGuild) != 0) return Access.In;
            return Access.Keypad;
        }

        /// <summary>Whether this guild is the one a house is shared with.</summary>
        public static bool InTheGuild(HouseStore.House house, long guildId)
            => house.SharedWithGuild && guildId > 0 && guildId == house.GuildId;

        /// <summary>
        /// Whether a typed code opens a house's door: its code, and not to somebody its guild rights
        /// keep out -- «nobody outside the guild», even with it.
        /// </summary>
        public static bool CodeOpens(HouseStore.House house, long guildId, string typed)
            => !(house.SharedWithGuild && (house.GuildRights & HouseStore.GuildRights.HouseNoGuild) != 0
                 && !InTheGuild(house, guildId))
               && HouseStore.Matches(house.AccessCode, typed);

        /// <summary>How somebody gets at a house chest.</summary>
        public enum ChestAccess { Open, Keypad, Refused }

        /// <summary>
        /// A chest is its owner's; the guild opens it without its code when the rights say so, and
        /// nobody outside the guild when they forbid it; anybody else, its code when it has one.
        /// </summary>
        public static ChestAccess ChestAccessFor(HouseStore.House house, long accountId, long guildId, string chestCode)
        {
            if (house.AccountId == accountId) return ChestAccess.Open;
            bool member = InTheGuild(house, guildId);
            if (member && (house.GuildRights & HouseStore.GuildRights.ChestGuild) != 0) return ChestAccess.Open;
            if (!member && house.SharedWithGuild && (house.GuildRights & HouseStore.GuildRights.ChestNoGuild) != 0)
                return ChestAccess.Refused;
            return chestCode.Length > 0 ? ChestAccess.Keypad : ChestAccess.Open;
        }

        /// <summary>The guild of whoever is in session, or zero.</summary>
        private static long MyGuildId()
        {
            try { return GuildStore.GuildOf(SessionContext.State.CharacterId)?.Id ?? 0; }
            catch { return 0; }
        }

        /// <summary>1/708 «Acción imposible. No puedes acceder a tu cofre de casa.»</summary>
        public const int ChestRefused = 708;

        /// <summary>1/336 «Mientras tengas tu casa en venta, no puedes compartirla con tu gremio.»</summary>
        public const int NoShareWhileOnSale = 336;

        /// <summary>0/18 «{0} te ha echado de su casa...»</summary>
        public const int ThrownOut = 18;

        /// <summary>The client has clicked the inside door.</summary>
        public static async Task LeaveAsync(NetworkStream? stream, int elementId, int skillId)
        {
            await SendAsync(stream, Op.Iwn, ConnectionProtocol.BuildElementInUse(
                elementId, skillId, SessionContext.State.CharacterId));
            await GoOutAsync(stream);
        }

        /// <summary>Out of the house, through the door one came in by: the leaving after the iwn.</summary>
        private static async Task GoOutAsync(NetworkStream? stream)
        {
            long here = SessionContext.State.MapId;

            // First the way he came in; if it is not known, the way the data says.
            long salidaMapa = SessionContext.State.HouseEntryMapId;
            int salidaCasilla = SessionContext.State.HouseEntryCell;
            if (salidaMapa == 0 || salidaMapa == here)
            {
                if (!Houses.TryGetWayBack(here, out var puerta))
                {
                    Console.WriteLine($"[Houses] Map {here} is not the interior of any known house.");
                    return;
                }
                salidaMapa = puerta.MapId;
                salidaCasilla = puerta.Cell;
            }

            SessionContext.State.MapId = salidaMapa;
            SessionContext.State.CellId = MapManager.GetNearestWalkableCell(salidaMapa, salidaCasilla);
            SessionContext.State.HouseEntryMapId = 0;
            SessionContext.State.HouseEntryCell = 0;
            SessionContext.State.HouseEntryElementId = 0;
            SessionContext.State.HouseEntryInstance = 0;
            DatabaseManager.SaveCurrentCharacter();

            await SessionRegistry.AnunciarMudanzaAsync(SessionContext.Current, here);

            await WriteAsync(stream, ConnectionProtocol.BuildActorLeft(SessionContext.State.CharacterId));
            await WriteAsync(stream, ConnectionProtocol.BuildLoadMap(salidaMapa));
            await WriteAsync(stream, ConnectionProtocol.BuildMapClock());
            await WriteAsync(stream, ConnectionProtocol.BuildMapDiscovered(salidaMapa));

            Console.WriteLine($"[Houses] Out of the interior {here} to map {salidaMapa}, " +
                              $"cell {SessionContext.State.CellId}.");
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
        /// client's skill for it, an inference. A building of many copies offers what any of them
        /// offers this viewer; the client's menu puts under each skill the copies it fits.
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

                var copies = HouseStore.CopiesOf(interactive.MapId, interactive.Element.Id, houseDoor.Copies);
                foreach (int skill in DoorSkillsFor(copies, viewerAccountId))
                {
                    var action = Find(interactive, skill);
                    if (action != null) visible.Add(action);
                }
                return visible;
            }

            var (doorMap, doorElement, instance) = CurrentHouse();
            if (doorMap == 0) return visible;
            var owner = HouseStore.Of(doorMap, doorElement, instance);
            if (!owner.Owned) return visible;
            foreach (var action in interactive.Actions)
            {
                if (action.SkillId == Houses.ChestOpenSkill
                    || (action.SkillId == Houses.ChestLockSkill && owner.AccountId == viewerAccountId))
                    visible.Add(action);
            }
            return visible;
        }

        /// <summary>
        /// The door skills a viewer is offered over the copies the street shows, in the order the
        /// jss declares them: enter; for a copy of his own the code, and sell or change its price;
        /// buy, for a copy nobody owns or somebody else sells.
        /// </summary>
        public static int[] DoorSkillsFor(IReadOnlyCollection<HouseStore.House> copies, long viewerAccountId)
        {
            var skills = new List<int> { Houses.EnterSkill };
            var mine = copies.Where(h => h.Owned && h.AccountId == viewerAccountId).ToList();
            if (mine.Count > 0)
            {
                skills.Add(Houses.CodeSkill);
                if (mine.Any(h => !h.ForSale)) skills.Add(Houses.SellSkill);
                if (mine.Any(h => h.ForSale)) skills.Add(Houses.PriceSkill);
            }
            if (copies.Any(h => !h.Owned || (h.ForSale && h.AccountId != viewerAccountId))) skills.Add(Houses.BuySkill);
            return skills.ToArray();
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
        /// The house this character is inside: the door he came in by and its copy, or the one the
        /// data gives for the interior when he reconnected inside. (0, 0, 0) when he is not in a house.
        /// </summary>
        public static (long MapId, int ElementId, int Instance) CurrentHouse() => HouseOf(SessionContext.State);

        /// <summary>
        /// The house somebody is inside, by the door he came in through, kept under its map, and
        /// the copy of the building: the house of his chests and the copy of the interior he sees.
        /// (0, 0, 0) outside a house.
        /// </summary>
        public static (long MapId, int ElementId, int Instance) HouseOf(SessionState state)
        {
            long here = state.MapId;
            if (!Houses.IsInterior(here)) return (0, 0, 0);

            long map = state.HouseEntryMapId;
            int element = state.HouseEntryElementId;
            if (map != 0 && element != 0 && Houses.TryGetDoor(map, element, out var door) && door.InteriorMapId == Houses.HallOf(here))
            {
                int instance = door.HasCopy(state.HouseEntryInstance) ? state.HouseEntryInstance : Houses.FirstInstance;
                return (Houses.CanonicalMapOf(map, element), element, instance);
            }

            return Houses.TryGetWayBack(here, out var wayBack)
                ? (Houses.CanonicalMapOf(wayBack.MapId, wayBack.ElementId), wayBack.ElementId, Houses.FirstInstance)
                : (0, 0, 0);
        }

        /// <summary>
        /// At the world entry inside a house whose door was not kept -- the way in is not saved --:
        /// one of the account's own houses that leads there, so its owner is in his own copy, with
        /// his chests. Anybody else is in the data's.
        /// </summary>
        public static void OnWorldEntry(long accountId)
        {
            var state = SessionContext.State;
            if (!Houses.IsInterior(state.MapId) || state.HouseEntryElementId != 0) return;
            foreach (var owned in HouseStore.OwnedBy(accountId))
            {
                if (!Houses.TryGetDoor(owned.MapId, owned.ElementId, out var door)) continue;
                if (door.InteriorMapId != Houses.HallOf(state.MapId)) continue;
                state.HouseEntryMapId = door.MapId;
                state.HouseEntryCell = door.Cell;
                state.HouseEntryElementId = door.ElementId;
                state.HouseEntryInstance = owned.Instance;
                return;
            }
        }

        // ─── The plaque ─────────────────────────────────────────────────────────

        /// <summary>What the plaque of this house says now.</summary>
        public static HouseProtocol.Plaque PlaqueOf(HouseStore.House house, Houses.Door door)
        {
            string name = "", tag = "";
            if (house.Owned) (name, tag) = AccountTagOf(house.AccountId);
            return new HouseProtocol.Plaque
            {
                Instance = house.Instance,
                OwnerName = name,
                OwnerTag = tag,
                Price = HouseStore.AskingPrice(house, door.Price),
                Locked = house.Owned && house.Locked,
                Rooms = door.Rooms,
                Chests = Houses.ChestCountOf(door.InteriorMapId),
                Skills = Houses.PlaqueSkillsOf(door.InteriorMapId),
                GuildBlock = GuildBlockOf(house),
            };
        }

        /// <summary>
        /// The guild a house is shared with, as the plaque carries it (f3): the guild block of the
        /// guild messages -- emblem, id, name, level --, the same as Pandala's «Guild-Sample».
        /// </summary>
        public static byte[]? GuildBlockOf(HouseStore.House house)
        {
            if (!house.SharedWithGuild) return null;
            try
            {
                var guild = GuildStore.ById(house.GuildId);
                return guild == null ? null : GuildProtocol.GuildBlock(guild).Build();
            }
            catch
            {
                return null;
            }
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
        /// street that can be owned. A house nobody owns is declared too, on sale at its model's
        /// price: without it the client knows no house behind the door and drops the buyer's khr.
        /// No plaque of a free house was ever captured, but the client takes this one: it shows the
        /// house as abandoned and on sale at that price, and opens the buyer's window. A building of
        /// many copies sends one plaque per copy the street is shown, as the capture's eleven.
        /// </summary>
        public static void AddToMap(Pb jss, long mapId)
        {
            if (Houses.IsInterior(mapId))
            {
                var (doorMap, doorElement, instance) = CurrentHouse();
                if (doorMap != 0 && Houses.TryGetDoor(doorMap, doorElement, out var door) && door.IsOwnable)
                {
                    var house = HouseStore.Of(doorMap, doorElement, instance);
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
                var copies = HouseStore.CopiesOf(mapId, door.ElementId, door.Copies);
                jss.Msg(9, HouseProtocol.BuildOnMap(Houses.HouseIdOf(mapId, door.ElementId), door.Model,
                    new long[] { door.ElementId }, copies.Select(h => PlaqueOf(h, door))));
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

            int houseId = (int)VarOf(izv, 2);
            int instance = (int)VarOf(izv, 1);
            if (!Houses.TryGetByHouseId(houseId, out var door)) return false;
            if (!door.HasCopy(instance)) instance = Houses.FirstInstance;

            var house = HouseStore.Of(door.MapId, door.ElementId, instance);
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
            => HouseProtocol.BuildAccountHouses(HouseStore.OwnedBy(accountId).Select(OwnedOf).Where(o => o != null)!);

        /// <summary>A house as the lists carry it, with where its street is; null for a door that is not a house.</summary>
        private static HouseProtocol.Owned? OwnedOf(HouseStore.House house)
        {
            if (!Houses.TryGetDoor(house.MapId, house.ElementId, out var door)) return null;
            var street = MapManager.GetMapInfo(house.MapId);
            return new HouseProtocol.Owned
            {
                HouseId = Houses.HouseIdOf(house.MapId, house.ElementId),
                Model = door.Model,
                StreetMapId = house.MapId,
                WorldX = street?.PosX ?? 0,
                WorldY = street?.PosY ?? 0,
                SubAreaId = street?.SubAreaId ?? 0,
                Plaque = PlaqueOf(house, door),
                GuildRights = house.SharedWithGuild ? house.GuildRights : 0,
            };
        }

        /// <summary>
        /// At the world entry, a guild member's guild houses (jjs): what the guild window's «Casas»
        /// tab lists. Nothing for whoever has no guild.
        /// </summary>
        public static async Task SendGuildHousesAsync(NetworkStream? stream, long characterId)
        {
            long guildId;
            try { guildId = GuildStore.GuildOf(characterId)?.Id ?? 0; }
            catch { return; }
            if (guildId == 0) return;
            var houses = HouseStore.SharedWith(guildId).Select(OwnedOf).Where(o => o != null)!;
            await SendAsync(stream, Op.Jjs, HouseProtocol.BuildGuildHouses(houses!));
        }

        /// <summary>The guild's members on line are told a house is theirs now (jjp), or not any more (jjt).</summary>
        private static async Task TellTheGuildAsync(long guildId, HouseStore.House house, Houses.Door door)
        {
            if (guildId <= 0) return;
            var guild = GuildStore.ById(guildId);
            if (guild == null) return;
            byte[] frame = house.GuildId == guildId && OwnedOf(house) is { } owned
                ? ConnectionProtocol.Push(Op.Jjp, HouseProtocol.BuildGuildHouse(owned))
                : ConnectionProtocol.Push(Op.Jjt, HouseProtocol.BuildGuildHouseGone(Houses.HouseIdOf(door.MapId, door.ElementId), house.Instance));
            await GuildHandler.TellEveryoneAsync(guild, frame);
        }

        // ─── Selling ────────────────────────────────────────────────────────────

        /// <summary>
        /// The owner's sale window: khr with the price the house is at, or its model's when it is
        /// not on sale (sale frame 3: 2,500,000, the Casa grande de Bonta's).
        /// </summary>
        private static async Task OpenSaleAsync(NetworkStream? stream, int elementId, int skillId, int instance)
        {
            long map = SessionContext.State.MapId;
            if (!Houses.TryGetDoor(map, elementId, out var door) || !door.HasCopy(instance)) return;
            var house = HouseStore.Of(map, elementId, instance);
            if (!IsOwner(house)) return;

            await SendAsync(stream, Op.Iwn, ConnectionProtocol.BuildElementInUse(elementId, skillId, SessionContext.State.CharacterId));

            long price = house.ForSale ? house.Price : door.Price;
            SessionContext.State.HouseDialog = new Dialog
            {
                Kind = DialogKind.Sale, DoorMapId = map, DoorElementId = elementId, Instance = instance, Price = price,
            };
            await SendAsync(stream, Op.Khr, HouseProtocol.BuildSaleWindow(false, Houses.HouseIdOf(map, elementId),
                                                                          instance, price));
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
            int instance = dialog.Instance;
            long sharedWith = HouseStore.Of(dialog.DoorMapId, dialog.DoorElementId, instance).GuildId;
            if (!await HouseStore.SetPriceAsync(dialog.DoorMapId, dialog.DoorElementId, instance, account, newPrice))
            {
                await SendAsync(stream, Op.Kld, ConnectionProtocol.BuildDialogClosed(HouseProtocol.SaleClosed));
                return true;
            }

            int houseId = Houses.HouseIdOf(dialog.DoorMapId, dialog.DoorElementId);
            var house = HouseStore.Of(dialog.DoorMapId, dialog.DoorElementId, instance);
            var offSale = new HouseStore.House
            {
                MapId = house.MapId, ElementId = house.ElementId, Instance = instance, AccountId = house.AccountId,
                Price = 0, AccessCode = house.AccessCode,
            };

            // First the house off sale, whatever comes next; then, if it goes on sale, jjt and the
            // house at its price. That is the order of the sale's frames 13-17.
            await SendDoorAndPlaqueAsync(stream, door, offSale, account);
            if (house.ForSale)
            {
                await SendAsync(stream, Op.Jjt, HouseProtocol.BuildPutOnSale(instance, houseId));
                await SendDoorAndPlaqueAsync(stream, door, house, account);
                if (sharedWith > 0) await TellTheGuildAsync(sharedWith, house, door);
            }

            var (name, tag) = AccountTagOf(account);
            await SendAsync(stream, Op.Jaa, BuildAccountHouses(account));
            await SendAsync(stream, Op.Izu, HouseProtocol.BuildSellingUpdate(name, tag, houseId, house.ForSale ? house.Price : 0, instance));
            await SendAsync(stream, Op.Izz, HouseProtocol.BuildChanged(houseId, new long[] { door.ElementId }, PlaqueOf(house, door)));
            await SendAsync(stream, Op.Kld, ConnectionProtocol.BuildDialogClosed(HouseProtocol.SaleClosed));

            await TellTheStreetAsync(door, house);
            Console.WriteLine(house.ForSale
                ? $"[Houses] {door.MapId}/{door.ElementId}/{instance} on sale for {house.Price:N0} kamas."
                : $"[Houses] {door.MapId}/{door.ElementId}/{instance} taken off sale.");
            return true;
        }

        /// <summary>
        /// The door as this owner sees it (iwm), with this copy as it is to be shown, and the copy's
        /// plaque (izz).
        /// </summary>
        private static async Task SendDoorAndPlaqueAsync(NetworkStream? stream, Houses.Door door, HouseStore.House house, long viewer)
        {
            await SendAsync(stream, Op.Iwm, DoorChanged(door, CopiesWith(door, house), viewer));
            await SendAsync(stream, Op.Izz, HouseProtocol.BuildChanged(Houses.HouseIdOf(door.MapId, door.ElementId),
                new long[] { door.ElementId }, PlaqueOf(house, door)));
        }

        /// <summary>The copies the street is shown, with this one as it is about to be shown.</summary>
        private static List<HouseStore.House> CopiesWith(Houses.Door door, HouseStore.House house)
        {
            var copies = HouseStore.CopiesOf(door.MapId, door.ElementId, door.Copies);
            int at = copies.FindIndex(h => h.Instance == house.Instance);
            if (at >= 0) copies[at] = house;
            else
            {
                copies.Add(house);
                copies.Sort((a, b) => a.Instance.CompareTo(b.Instance));
            }
            return copies;
        }

        /// <summary>The door redeclared with the skills this viewer has now (iwm), over the copies the street shows.</summary>
        public static byte[] DoorChanged(Houses.Door door, long viewerAccountId)
            => DoorChanged(door, HouseStore.CopiesOf(door.MapId, door.ElementId, door.Copies), viewerAccountId);

        public static byte[] DoorChanged(Houses.Door door, IReadOnlyCollection<HouseStore.House> copies, long viewerAccountId)
        {
            var skills = new List<(int, int)>();
            foreach (int skill in DoorSkillsFor(copies, viewerAccountId)) skills.Add((DoorSkillInstance(door.ElementId, skill), skill));
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
        /// Everybody else on the street gets the door as they see it and the plaque, on every map
        /// the door stands on. Nobody else was there in the captures: an inference.
        /// </summary>
        private static async Task TellTheStreetAsync(Houses.Door door, HouseStore.House house)
        {
            byte[] plaque = ConnectionProtocol.Push(Op.Izz, HouseProtocol.BuildChanged(
                Houses.HouseIdOf(door.MapId, door.ElementId), new long[] { door.ElementId }, PlaqueOf(house, door)));
            var copies = CopiesWith(door, house);
            foreach (var session in Houses.MapsOfDoor(door.ElementId).SelectMany(SessionRegistry.OnMap))
            {
                if (session.Id == SessionContext.Current.Id) continue;
                try
                {
                    await session.SendAsync(ConnectionProtocol.Push(Op.Iwm, DoorChanged(door, copies, session.AccountId))).ConfigureAwait(false);
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
        private static async Task OpenPurchaseAsync(NetworkStream? stream, int elementId, int skillId, int instance)
        {
            long map = SessionContext.State.MapId;
            if (!Houses.TryGetDoor(map, elementId, out var door) || !door.IsOwnable || !door.HasCopy(instance)) return;
            var house = HouseStore.Of(map, elementId, instance);
            long price = HouseStore.AskingPrice(house, door.Price);
            if (price <= 0 || house.AccountId == SessionContext.Current.AccountId) return;

            await SendAsync(stream, Op.Iwn, ConnectionProtocol.BuildElementInUse(elementId, skillId, SessionContext.State.CharacterId));

            SessionContext.State.HouseDialog = new Dialog
            {
                Kind = DialogKind.Purchase, DoorMapId = map, DoorElementId = elementId, Instance = instance, Price = price,
            };
            await SendAsync(stream, Op.Khr, HouseProtocol.BuildSaleWindow(true, Houses.HouseIdOf(map, elementId),
                                                                          instance, price));
        }

        /// <summary>
        /// The buyer's yes: jal { f1: price }, seen from the client at the palace of the Lacs
        /// enchantés. jal is also the owner's kick, with a character for its number, so it is taken
        /// as the yes ONLY with a buyer's window open and only when its number is the price that
        /// window showed: a kick cannot buy a house by accident. jad is taken the same way.
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
            var (done, why) = await HouseStore.BuyAsync(dialog.DoorMapId, dialog.DoorElementId, dialog.Instance, door.Price,
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
                Console.WriteLine($"[Houses] {state.CharacterId} could not buy {door.MapId}/{door.ElementId}/{dialog.Instance}: {why}.");
                return true;
            }

            state.Kamas = done.BuyerKamas;
            await SendAsync(stream, Op.Ivf, ConnectionProtocol.BuildKamas(done.BuyerKamas));
            // The buyer's line in the chat, the client's own «Has perdido {0} kamas.»: it has no
            // sentence of its own for a house bought.
            await SendAsync(stream, Op.Lqn, ConnectionProtocol.BuildSystemMessage(InfoMessages.KamasLost,
                done.Price.ToString(CultureInfo.InvariantCulture)));

            var house = HouseStore.Of(door.MapId, door.ElementId, dialog.Instance);
            await SendDoorAndPlaqueAsync(stream, door, house, buyer);
            await SendAsync(stream, Op.Jaa, BuildAccountHouses(buyer));
            await SendAsync(stream, Op.Kld, ConnectionProtocol.BuildDialogClosed(HouseProtocol.SaleClosed));

            await TellTheStreetAsync(door, house);
            if (done.SellerAccountId > 0) await TellTheSellerAsync(done, state.CharacterName);

            Console.WriteLine($"[Houses] {door.MapId}/{door.ElementId}/{dialog.Instance} bought by account {buyer} for {done.Price:N0} kamas.");
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
        private static async Task OpenDoorKeypadAsync(NetworkStream? stream, int elementId, int skillId, int instance)
        {
            long map = SessionContext.State.MapId;
            var house = HouseStore.Of(map, elementId, instance);
            if (!IsOwner(house)) return;

            await SendAsync(stream, Op.Iwn, ConnectionProtocol.BuildElementInUse(elementId, skillId, SessionContext.State.CharacterId));
            SessionContext.State.HouseDialog = new Dialog
            {
                Kind = DialogKind.DoorCode, DoorMapId = map, DoorElementId = elementId, Instance = instance,
            };
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
                if (await HouseStore.SetAccessCodeAsync(dialog.DoorMapId, dialog.DoorElementId, dialog.Instance, account, code)
                    && Houses.TryGetDoor(dialog.DoorMapId, dialog.DoorElementId, out var door))
                {
                    var house = HouseStore.Of(dialog.DoorMapId, dialog.DoorElementId, dialog.Instance);
                    await SendDoorAndPlaqueAsync(stream, door, house, account);
                    await SendAsync(stream, Op.Khu, HouseProtocol.BuildCodeResult(wrong: false));
                    await TellTheStreetAsync(door, house);
                    Console.WriteLine($"[Houses] {dialog.DoorMapId}/{dialog.DoorElementId}: access code {(code.Length == 0 ? "removed" : "set")}.");
                }
            }
            else if (await HouseStore.SetChestCodeAsync(dialog.DoorMapId, dialog.DoorElementId, dialog.Instance, dialog.ChestElementId, account, code))
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
        /// frames 3889-3890. Right: the same kld and no khu, then in -- or the chest opens. A khu
        /// without a result is the owner's: the client (frx::bicj) answers it with «Código
        /// modificado», or «sin código» for one taken off, so it is not sent for a code typed right.
        /// False when no stranger's keypad is open.
        /// </summary>
        public static async Task<bool> UseCodeAsync(NetworkStream? stream, byte[] payload)
        {
            var dialog = OpenDialog(DialogKind.EnterCode, DialogKind.ChestKeypad);
            if (dialog == null || dialog.Answered) return false;

            byte[]? khw = ConnectionProtocol.ReadPayload(payload, Op.Khw);
            string typed = khw == null ? "" : TextOf(khw);
            dialog.Answered = true;
            SessionContext.State.HouseDialog = null;

            bool right = dialog.Kind == DialogKind.EnterCode
                ? CodeOpens(HouseStore.Of(dialog.DoorMapId, dialog.DoorElementId, dialog.Instance), MyGuildId(), typed)
                : HouseStore.Matches(HouseStore.ChestCodeOf(dialog.DoorMapId, dialog.DoorElementId, dialog.Instance, dialog.ChestElementId), typed);

            await SendAsync(stream, Op.Kld, ConnectionProtocol.BuildDialogClosed(HouseProtocol.CodeClosed));
            if (!right)
            {
                await SendAsync(stream, Op.Khu, HouseProtocol.BuildCodeResult(wrong: true));
                Console.WriteLine($"[Houses] {SessionContext.State.CharacterId} typed a wrong code at {dialog.DoorMapId}/{dialog.DoorElementId}.");
                return true;
            }

            if (dialog.Kind == DialogKind.EnterCode)
            {
                if (SessionContext.State.MapId == dialog.DoorMapId && Houses.TryGetDoor(dialog.DoorMapId, dialog.DoorElementId, out var door))
                    await GoInsideAsync(stream, door, dialog.Instance);
            }
            else
            {
                await OpenChestAsync(stream, dialog.DoorMapId, dialog.DoorElementId, dialog.Instance, dialog.ChestElementId);
            }
            return true;
        }

        // ─── The guild ──────────────────────────────────────────────────────────

        /// <summary>
        /// jai { f3: house, f4: instance }: the guild house window asks what the house's guild may
        /// do, answered with jak. Only the owner's: the window is his. False when it names no house.
        /// </summary>
        public static async Task<bool> GuildRightsAsync(NetworkStream? stream, byte[] payload)
        {
            byte[]? jai = ConnectionProtocol.ReadPayload(payload, Op.Jai);
            if (jai == null) return false;
            int houseId = (int)VarOf(jai, 3);
            int instance = (int)VarOf(jai, 4);
            if (!Houses.TryGetByHouseId(houseId, out var door)) return false;
            if (!door.HasCopy(instance)) instance = Houses.FirstInstance;
            var house = HouseStore.Of(door.MapId, door.ElementId, instance);
            if (!IsOwner(house)) return true;
            await SendAsync(stream, Op.Jak, HouseProtocol.BuildGuildRights(houseId, instance, GuildBlockOf(house), house.GuildRights));
            return true;
        }

        /// <summary>
        /// jao { f1: share, f2: house, f3: instance, f4: rights }: the owner shares his house with
        /// his guild, with these rights, or stops sharing it. Not while it is on sale (1/336). The
        /// client is told with jak, the street with the plaque (izz) and the owner's list (jaa).
        /// </summary>
        public static async Task<bool> ShareWithGuildAsync(NetworkStream? stream, byte[] payload)
        {
            byte[]? jao = ConnectionProtocol.ReadPayload(payload, Op.Jao);
            if (jao == null) return false;
            bool share = VarOf(jao, 1) != 0;
            int houseId = (int)VarOf(jao, 2);
            int instance = (int)VarOf(jao, 3);
            int rights = (int)VarOf(jao, 4);
            if (!Houses.TryGetByHouseId(houseId, out var door)) return false;
            if (!door.HasCopy(instance)) instance = Houses.FirstInstance;
            var house = HouseStore.Of(door.MapId, door.ElementId, instance);
            if (!IsOwner(house)) return true;

            long guildId = 0;
            if (share)
            {
                if (house.ForSale)
                {
                    await SendAsync(stream, Op.Lqn, ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, NoShareWhileOnSale));
                    return true;
                }
                guildId = MyGuildId();
                if (guildId == 0) return true;
            }
            long before = house.GuildId;
            await HouseStore.SetGuildShareAsync(door.MapId, door.ElementId, instance, house.AccountId, guildId, rights);

            house = HouseStore.Of(door.MapId, door.ElementId, instance);
            if (before > 0 && before != house.GuildId) await TellTheGuildAsync(before, house, door);
            if (house.GuildId > 0) await TellTheGuildAsync(house.GuildId, house, door);
            await SendAsync(stream, Op.Jak, HouseProtocol.BuildGuildRights(houseId, instance, GuildBlockOf(house), house.GuildRights));
            await TellTheStreetAsync(door, house);
            await SendAccountHousesAsync(stream);
            Console.WriteLine($"[Houses] House {houseId}/{instance} " + (guildId > 0 ? $"shared with guild {guildId}, rights {house.GuildRights}." : "no longer shared."));
            return true;
        }

        /// <summary>
        /// jal { f1: character }: the owner throws somebody out of his house. He goes out through
        /// the door he came in by, told «{0} te ha echado de su casa...» (0/18).
        /// </summary>
        public static async Task<bool> KickAsync(NetworkStream? stream, byte[] payload)
        {
            byte[]? jal = ConnectionProtocol.ReadPayload(payload, Op.Jal);
            if (jal == null) return false;
            long target = VarOf(jal, 1);
            var mine = CurrentHouse();
            if (mine.MapId == 0 || target == SessionContext.State.CharacterId) return true;
            if (!IsOwner(HouseStore.Of(mine.MapId, mine.ElementId, mine.Instance))) return true;

            var session = SessionRegistry.FindByCharacter(target);
            if (session == null || HouseOf(session.State) != mine) return true;
            string owner = SessionContext.State.CharacterName;
            using (SessionContext.Push(session))
            {
                await GoOutAsync(session.Stream);
                await SendAsync(session.Stream, Op.Lqn, ConnectionProtocol.BuildSystemMessage(ThrownOut, owner));
            }
            Console.WriteLine($"[Houses] {owner} throws {target} out of the house {mine.MapId}/{mine.ElementId}/{mine.Instance}.");
            return true;
        }

        private static long VarOf(byte[] body, int number)
        {
            foreach (var field in ProtoMessage.Parse(body).Fields)
            {
                if (field.FieldNumber == number && field.WireType == 0) return field.VarIntValue;
            }
            return 0;
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

            var (doorMap, doorElement, instance) = CurrentHouse();
            if (doorMap == 0) return;
            var house = HouseStore.Of(doorMap, doorElement, instance);
            if (!house.Owned) return;
            bool mine = IsOwner(house);

            await SendAsync(stream, Op.Iwn, ConnectionProtocol.BuildElementInUse(
                chest.Element.Id, action.SkillId, SessionContext.State.CharacterId));

            if (action.SkillId == Houses.ChestLockSkill)
            {
                SessionContext.State.HouseDialog = new Dialog
                {
                    Kind = DialogKind.ChestCode, DoorMapId = doorMap, DoorElementId = doorElement, Instance = instance,
                    ChestElementId = chest.Element.Id,
                };
                await SendAsync(stream, Op.Kia, HouseProtocol.BuildCodeKeypad(toGetIn: false));
                return;
            }

            // A guild may open it, or be kept from it; a locked chest asks anybody else its code,
            // as a locked door does: an inference.
            var access = ChestAccessFor(house, SessionContext.Current.AccountId, MyGuildId(),
                                        HouseStore.ChestCodeOf(doorMap, doorElement, instance, chest.Element.Id));
            if (access == ChestAccess.Refused)
            {
                await SendAsync(stream, Op.Lqn, ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, ChestRefused));
                return;
            }
            if (!mine && access == ChestAccess.Keypad)
            {
                SessionContext.State.HouseDialog = new Dialog
                {
                    Kind = DialogKind.ChestKeypad, DoorMapId = doorMap, DoorElementId = doorElement, Instance = instance,
                    ChestElementId = chest.Element.Id,
                };
                await SendAsync(stream, Op.Kia, HouseProtocol.BuildCodeKeypad(toGetIn: true));
                return;
            }

            await OpenChestAsync(stream, doorMap, doorElement, instance, chest.Element.Id);
        }

        /// <summary>kci { 100, 4 } and what is inside: frames 8-9 of the house chest capture.</summary>
        private static Task OpenChestAsync(NetworkStream? stream, long doorMap, int doorElement, int instance, int chestElement)
            => StorageHandler.OpenAsync(stream, new StorageHandler.Window
            {
                Kind = StorageHandler.Kind.HouseChest,
                Place = StorageStacks.HouseChest(doorMap, doorElement, instance, chestElement),
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
