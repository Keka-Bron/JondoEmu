using System;
using System.Collections.Generic;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>The game action behind an interactive skill.</summary>
    public enum InteractiveActionKind
    {
        Zaap,
        Chest,
        Lottery,

        /// <summary>The short transport inside Bonta and Brakmar.</summary>
        Zaapi,

        /// <summary>A temporal anomaly's place: its exit back to the anomaly's zaap (AnomalyHandler).</summary>
        AnomalyExit,

        /// <summary>A clock of Noximilian's Workshop, which tells the hour (AnomalyHandler).</summary>
        AnomalyClock,

        /// <summary>A way of the Almanax's temple, which leads to today's hall or room (AlmanaxTemple).</summary>
        AlmanaxWay,

        /// <summary>A way of Gargandias's temple: its door, the grate, the stairs (GargandiasHandler).</summary>
        GargandiasWay,

        /// <summary>A kart of route 666: its network's stops in the zaapi's window (Transports).</summary>
        Transport,

        /// <summary>The bin: the public store of what people throw away.</summary>
        Bin,

        /// <summary>A house's street door.</summary>
        HouseDoor,

        /// <summary>The inside door, the one leading back to the street.</summary>
        HouseExit,

        /// <summary>An instant passage between two maps, outside the house system.</summary>
        Teleport,

        /// <summary>Un recurso de oficio: trigo, fresno, caladero, mineral.</summary>
        Gather,

        /// <summary>
        /// A workshop station: it opens the craft window, or the smithmagic one when the skill is
        /// a magus'. Both are the same kgq with the skill in it.
        /// </summary>
        Workshop,

        /// <summary>The Well of Infinite Dreams, which opens the dream window.</summary>
        Dream,

        /// <summary>One of a room's three doors, which leads to the row below.</summary>
        DreamDoor,

        /// <summary>The altar of the Guild Temple, which opens the founding editor.</summary>
        GuildFounding,

        /// <summary>The Temple of Alliances' door, which opens the alliance founding editor.</summary>
        AllianceFounding,

        /// <summary>A marketplace counter: it opens the marketplace to buy (kdw).</summary>
        Marketplace,

        /// <summary>A chest inside a house: open it (104) or lock it (105).</summary>
        HouseChest,

        /// <summary>The guild chest, in the banks: "Utilizar" (184) opens the guild's.</summary>
        GuildChest,

        /// <summary>A guild raid's Luminarium lantern fish (GuildRaidLuminarium).</summary>
        RaidLantern,

        /// <summary>The Cangrancio's statues, levers of its enigma (GuildRaidExecrabe).</summary>
        RaidStatue,

        /// <summary>The Santuario's manuscript, whose pages are torn one per zone (GuildRaidSanctuary).</summary>
        RaidManuscript,

        /// <summary>A paper flower of the Santuario's Obra monocromática (GuildRaidMonochrome).</summary>
        RaidFlower,

        /// <summary>A square of one of the Patio de Efedra's miniature gardens (GuildRaidEnclave).</summary>
        RaidGardenSquare,

        /// <summary>The button that validates a miniature garden (GuildRaidEnclave).</summary>
        RaidGardenButton,

        /// <summary>A paper boat of the Obra, revealed onto the Reserva's boards (GuildRaidBelladona).</summary>
        RaidPaperBoat,

        /// <summary>A ball of a Reserva board, a letter or a number of the shot (GuildRaidBelladona).</summary>
        RaidCannonBall,

        /// <summary>A piece or a decoy of Belladona's chessboards in the Reserva (GuildRaidEfedra).</summary>
        RaidChessPiece,

        /// <summary>A paddock's gate: it opens the breeding window on its paddock (BreedingHandler).</summary>
        PaddockGate,

        /// <summary>The neutral prism beside the Temple of Alliances: «Reciclar recursos» (RecycleHandler).</summary>
        Recycle,

        /// <summary>The Temple of Alliances' board: «Consultar» opens the top tax collectors (TaxCollectorHandler).</summary>
        TopTaxCollectors,

        /// <summary>A Travelers' Tower room's teleporter: to the room its skill names (TravelersTower).</summary>
        TowerRoom,

        /// <summary>The dimensional portal's mechanism: opens the portal to a dimension, or closes it (TravelersTower).</summary>
        TowerCreator,

        /// <summary>The dimensional portal: into the dimension it is open to (TravelersTower).</summary>
        TowerPortal,

        /// <summary>A divine dimension's portal back to the tower (TravelersTower).</summary>
        DimensionExit,

        /// <summary>A divine dimension's model in the localization room: launches its legendary hunt (TreasureHuntHandler).</summary>
        FissureModel,

        /// <summary>The Malle aux trésors' Anutrof box: launches the treasure hunt of Dhain's quest (TreasureHuntHandler).</summary>
        TreasureBox,

        /// <summary>The Malle aux trésors' planning table: the legendary hunts' window (TreasureHuntHandler).</summary>
        PlanningTable,
    }

    /// <summary>A skill offered by an interactive element.</summary>
    public sealed class InteractiveAction
    {
        internal InteractiveAction(InteractiveActionKind kind, int skillId, int skillInstanceId, int skillNameId = 0)
        {
            Kind = kind;
            SkillId = skillId;
            SkillInstanceId = skillInstanceId;
            SkillNameId = skillNameId;
        }

        public InteractiveActionKind Kind { get; }
        public int SkillId { get; }
        public int SkillInstanceId { get; }

        /// <summary>
        /// The name the client gives the skill, a row of its SkillNamesDataRoot, or 0 for the
        /// skill's own: the f3 of the skill in the jss, which the client looks up before the
        /// skill's name (ens::badj).
        /// </summary>
        public int SkillNameId { get; }
    }

    /// <summary>
    /// An interactive element registered on a map, with all the skills it offers.
    /// Although the three current interactives only have one, the protocol allows several.
    /// </summary>
    public sealed class RegisteredInteractive
    {
        private readonly List<InteractiveAction> _actions = new List<InteractiveAction>();

        internal RegisteredInteractive(long mapId, Interactives.Element element, int type)
        {
            MapId = mapId;
            Element = element;
            Type = type;
        }

        public long MapId { get; }
        public Interactives.Element Element { get; }
        public int Type { get; }
        public IReadOnlyList<InteractiveAction> Actions => _actions;

        internal void Add(InteractiveActionKind kind, int skillId) => AddNamed(kind, skillId, 0);

        /// <summary>An action whose skill the client shows by one of its SkillNamesDataRoot names.</summary>
        internal void AddNamed(InteractiveActionKind kind, int skillId, int skillNameId)
        {
            // The first action keeps exactly the historical uid. If a future element offers
            // several, the following ones get the contiguous uids still free.
            int instance = Interactives.SkillInstanceOf(Element.Id);
            while (ContainsInstance(instance)) instance++;
            _actions.Add(new InteractiveAction(kind, skillId, instance, skillNameId));
        }

        /// <summary>A named action with the skill instance it is to have.</summary>
        internal void AddNamed(InteractiveActionKind kind, int skillId, int skillNameId, int instance)
        {
            if (ContainsInstance(instance))
                throw new InvalidOperationException($"Skill instance {instance} declared twice on element {Element.Id}.");
            _actions.Add(new InteractiveAction(kind, skillId, instance, skillNameId));
        }

        /// <summary>
        /// An action with the skill instance it is to have. For the elements whose skills are
        /// offered per viewer -- a house's door and chests -- each skill keeps one instance for
        /// good, whichever of them a viewer gets.
        /// </summary>
        internal void Add(InteractiveActionKind kind, int skillId, int instance)
        {
            if (ContainsInstance(instance))
                throw new InvalidOperationException($"Skill instance {instance} declared twice on element {Element.Id}.");
            _actions.Add(new InteractiveAction(kind, skillId, instance));
        }

        private bool ContainsInstance(int instance)
        {
            foreach (var action in _actions)
            {
                if (action.SkillInstanceId == instance) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Single registry of the interactives Jondo knows how to declare and execute.
    ///
    /// The real key is (map, element); the skill instance serves to check the
    /// <c>iwo</c> request. The concrete providers (zaap, chest and lottery for now) are only
    /// used during <see cref="Initialize"/>. From then on, the network does not need to know them.
    /// </summary>
    public static class InteractiveRegistry
    {
        /// <summary>
        /// The registry as one set: <see cref="Initialize"/> fills a new one on its own thread and
        /// publishes it whole, so whoever reads meanwhile -- a map being sent, or a test running
        /// beside -- sees the old registry or the new one. It used to be emptied and refilled in
        /// place, and a reader enumerating a map's entries then found the list changing under it.
        /// </summary>
        private sealed class Tables
        {
            public readonly Dictionary<long, List<RegisteredInteractive>> ByMap = new();
            public readonly Dictionary<(long MapId, int ElementId), RegisteredInteractive> ByElement = new();
        }

        private static volatile Tables _published = new();
        [ThreadStatic] private static Tables? _building;
        private static Tables Current => _building ?? _published;

        private static Dictionary<long, List<RegisteredInteractive>> _byMap => Current.ByMap;
        private static Dictionary<(long MapId, int ElementId), RegisteredInteractive> _byElement => Current.ByElement;

        public static int Count => _byElement.Count;

        public static void Initialize()
        {
            var fresh = new Tables();
            _building = fresh;
            try
            {
                Fill();
            }
            finally
            {
                _building = null;
            }
            _published = fresh;
        }

        /// <summary>The registering proper, into the set being built (<see cref="Tables"/>).</summary>
        private static void Fill()
        {

            // This order keeps exactly the historical order inside the jss.
            foreach (long mapId in Interactives.MapIds)
            {
                foreach (var element in Interactives.ZaapElements(mapId))
                    Register(mapId, element, Interactives.TypeOfZaap(mapId, element),
                        InteractiveActionKind.Zaap, Interactives.UseSkill);
            }

            foreach (long mapId in Interactives.MapIds)
            {
                var element = Merkasako.ChestOf(mapId);
                if (element.Id != 0)
                    Register(mapId, element, Merkasako.ChestType,
                        InteractiveActionKind.Chest, Merkasako.ChestSkill);
            }

            foreach (long mapId in Interactives.MapIds)
            {
                var element = Lottery.Of(mapId);
                if (element.Id != 0)
                    Register(mapId, element, Lottery.Type,
                        InteractiveActionKind.Lottery, Lottery.Skill);
            }

            // The Well of Dreams. It is in the client's data as one more element -539616,
            // graphic 90166, cell 370 of the Astral Plane map- but without a declared action
            // the client does not even let it be clicked: it is an ornament.
            //
            // The element and the skill come from the f11 of that map's real jss, not from the iwo: the
            // iwo returns the instance UID, and taking it for the skill is what left the well
            // unclickable. See Dreams.HabilidadDelPozo.
            foreach (var pozo in Interactives.ElementsOf(Dreams.MapaDelPozo))
            {
                if (pozo.Id != Dreams.ElementoDelPozo) continue;
                Register(Dreams.MapaDelPozo, pozo, Dreams.TipoDelPozo,
                         InteractiveActionKind.Dream, Dreams.HabilidadDelPozo);
                Register(Dreams.MapaDelPozo, pozo, Dreams.TipoDelPozo,
                         InteractiveActionKind.Dream, Dreams.SegundaHabilidadDelPozo);
            }

            // And the rooms' doors. Without this the same happens as happened with the well: the
            // client draws them -they are in its own map data- and does not let them be clicked, so
            // the player enters the dream, stands in the entrance room and has nowhere to go
            // on. No error, again.
            //
            // Measured on the eight rooms that appear in the captures, 48 declarations and all
            // alike: f11 { f1: 1, f4 { uid, 184 }, f5: the element, f6: -1 }.
            int puertas = 0;
            foreach (long sala in Dreams.TodosLosMapasDeSala())
            {
                foreach (var puerta in Interactives.ElementsOf(sala))
                {
                    // The favour room's centrepiece is neither a door nor a fountain: left alone.
                    if (!Dreams.IsDoorOrFountain(puerta.Gfx)) continue;

                    // The Fontaine onirique of a fountain room is not a door: skill 355,
                    // "Consultar", as the long capture declares element 539708.
                    int skill = puerta.Gfx == Dreams.FountainGfx ? Dreams.FountainSkill : Dreams.HabilidadDelPozo;
                    Register(sala, puerta, Dreams.TipoDelPozo, InteractiveActionKind.DreamDoor, skill);
                    puertas++;
                }
            }

            if (puertas > 0) Console.WriteLine($"[Dreams] {puertas} room door(s) declared.");

            // The zaapis and the bins are recognised by their GRAPHIC and there are dozens, so they
            // are registered in bulk instead of one by one like the zaap or the lottery.
            foreach (long mapId in Interactives.MapIds)
            {
                foreach (var element in Zaapis.ElementsOn(mapId))
                    Register(mapId, element, Zaapis.Type, InteractiveActionKind.Zaapi, Zaapis.UseSkill);
            }

            // The karts of route 666, on their stop and on the neighbour whose border shows them.
            foreach (var network in Transports.Networks)
                foreach (var stop in network.Stops)
                    foreach (long mapId in stop.Maps)
                        foreach (var vehicle in Transports.VehiclesOn(mapId))
                        {
                            if (_byElement.ContainsKey((mapId, vehicle.Id))) continue;
                            Register(mapId, vehicle, Transports.Type, InteractiveActionKind.Transport, Transports.UseSkill);
                        }

            foreach (long mapId in Interactives.MapIds)
            {
                foreach (var element in Bins.On(mapId))
                    Register(mapId, element, Bins.Type, InteractiveActionKind.Bin, Bins.UseSkill);
            }

            // Houses go in two rounds: the street doors, which are on world maps,
            // and the inside ones, which are on interiors that do not appear in Interactives.MapIds.
            // A door of a house that can be owned carries all five door skills, each with its own
            // instance; who is offered which is decided per viewer when the map is sent
            // (HouseHandler.VisibleActions). Any other door enters, as it always did.
            foreach (long mapId in Interactives.MapIds)
            {
                foreach (var door in Houses.On(mapId))
                {
                    var element = new Interactives.Element(door.ElementId, door.Cell, door.Gfx);
                    var skills = door.IsOwnable ? Houses.OwnableDoorSkills : new[] { Houses.EnterSkill };
                    foreach (int skill in skills)
                        Register(mapId, element, Houses.DoorType, InteractiveActionKind.HouseDoor, skill,
                                 Handlers.HouseHandler.DoorSkillInstance(door.ElementId, skill));
                }
            }

            foreach (long interior in Houses.Interiors)
            {
                if (!Houses.TryGetExit(interior, out var exit)) continue;
                Register(interior, new Interactives.Element(exit.ElementId, exit.Cell, exit.Gfx),
                         Houses.ExitType, InteractiveActionKind.HouseExit, Houses.ExitSkill);

                // A room with two suns -- Sufokia's small house, 505761 and 505762 -- is left by
                // either: the other sun of the exit's drawing leads out too, unless it is a passage.
                foreach (var element in Interactives.ElementsOf(interior))
                {
                    if (element.Id == exit.ElementId || element.Gfx != exit.Gfx || exit.IsRealDoor) continue;
                    if (TeleportManager.TryGet(interior, element.Id, out _)) continue;
                    Register(interior, element, Houses.ExitType, InteractiveActionKind.HouseExit, Houses.ExitSkill);
                }
            }

            // And the chests inside, type 85 with "Abrir" and "Poner el cerrojo" as the interior's
            // jss declares them ("entrar en mi casa", frame 16). Whose chest it is depends on the
            // door one came in by, so the same element is every house's chest of that interior.
            int houseChests = 0;
            foreach (long interior in Houses.Interiors)
            {
                foreach (var chest in Houses.ChestsIn(interior))
                {
                    if (_byElement.ContainsKey((interior, chest.Id))) continue;
                    Register(interior, chest, Houses.ChestType, InteractiveActionKind.HouseChest, Houses.ChestOpenSkill,
                             Handlers.HouseHandler.DoorSkillInstance(chest.Id, Houses.ChestOpenSkill));
                    Register(interior, chest, Houses.ChestType, InteractiveActionKind.HouseChest, Houses.ChestLockSkill,
                             Handlers.HouseHandler.DoorSkillInstance(chest.Id, Houses.ChestLockSkill));
                    houseChests++;
                }
            }
            if (houseChests > 0) Console.WriteLine($"[Houses] {houseChests} chests declared inside the interiors.");

            // The guild chests of the banks, by their graphic: type 388, "Utilizar", as the Bonta
            // bank's jss declares element 524415.
            foreach (long mapId in Interactives.MapIds)
            {
                foreach (var chest in GuildChests.On(mapId))
                {
                    if (_byElement.ContainsKey((mapId, chest.Id))) continue;
                    Register(mapId, chest, GuildChests.Type, InteractiveActionKind.GuildChest, GuildChests.UseSkill);
                }
            }

            // The altar of the Guild Temple. Like the well: it is in the client's data as
            // one more element -480310, cell 326 of map 106169344- and without a declared
            // action it is an ornament. The skill and the element come from the iwo/iwn of the capture
            // of founding «Jondo»: iwo {3597, 480310} → iwn {1, 480310, f4 184} and behind it the jjc that
            // opens the editor. See GuildHandler.OpenFoundingAsync.
            foreach (var altar in Interactives.ElementsOf(Handlers.GuildHandler.FoundingMap))
            {
                if (altar.Id != Handlers.GuildHandler.FoundingAltar) continue;
                Register(Handlers.GuildHandler.FoundingMap, altar, Handlers.GuildHandler.FoundingType,
                         InteractiveActionKind.GuildFounding, Handlers.GuildHandler.FoundingSkill);
            }

            // The Temple of Alliances' blue door, 481519: the capture "ir a templo alianza-intentar
            // entrar sin alianzagema" presses it -- iwo {18601, 481519} → iwn {1, 481519, f4 184} --
            // and is told who may enter. And the book of the room it lets into, which founds. See
            // AllianceHandler.UseTempleAsync.
            foreach (var (map, element, skill) in new[]
                     {
                         (Handlers.AllianceHandler.TempleMap, Handlers.AllianceHandler.TempleDoor, Handlers.AllianceHandler.TempleDoorSkill),
                         (Handlers.AllianceHandler.InnerRoom, Handlers.AllianceHandler.FoundingBook, Handlers.AllianceHandler.FoundingBookSkill),
                     })
            {
                foreach (var found in Interactives.ElementsOf(map))
                {
                    if (found.Id != element) continue;
                    Register(map, found, Interactives.TypeOfGfx(found.Gfx), InteractiveActionKind.AllianceFounding, skill);
                }
            }

            // The neutral prism beside the Temple of Alliances: interactive type 451 «Prisma neutral»
            // with skill 457 «Reciclar recursos» (Translations 1148930-1148932), the crystal with the
            // alliance emblem at cell 172 of 115081729. See RecycleHandler.
            foreach (var prism in Interactives.ElementsOf(Handlers.RecycleHandler.PrismMap))
            {
                if (prism.Id != Handlers.RecycleHandler.PrismElement) continue;
                Register(Handlers.RecycleHandler.PrismMap, prism, Handlers.RecycleHandler.PrismType,
                         InteractiveActionKind.Recycle, Handlers.RecycleHandler.RecycleSkill);
            }

            // The board in front of the Temple of Alliances, 488131 at cell 118: the same capture
            // uses it -- iwo {18599, 488131} → iwn {1, 488131, f4 293}, skill 293 «Consultar» -- and
            // gets the top tax collectors (hks). See TaxCollectorHandler.ShowTopsAsync.
            foreach (var board in Interactives.ElementsOf(Handlers.TaxCollectorHandler.TopsMap))
            {
                if (board.Id != Handlers.TaxCollectorHandler.TopsBoard) continue;
                Register(Handlers.TaxCollectorHandler.TopsMap, board, Interactives.TypeOfGfx(board.Gfx),
                         InteractiveActionKind.TopTaxCollectors, Handlers.TaxCollectorHandler.TopsSkill);
            }

            // The six paddocks' gates, type 120 «Cercado», each with the skill that opens its paddock
            // (423 to 428), as the client's breeding data places them (Managers.Breeding).
            foreach (var paddock in Breeding.Paddocks)
            {
                foreach (var gate in Interactives.ElementsOf(paddock.MapId))
                {
                    if (gate.Id != paddock.Element || _byElement.ContainsKey((paddock.MapId, gate.Id))) continue;
                    Register(paddock.MapId, gate, Breeding.GateType, InteractiveActionKind.PaddockGate, paddock.Skill);
                }
            }

            // The Travelers' Tower: its rooms' teleporters, the dimensional portal's mechanism and
            // portal, and the dimensions' portals back. Each skill with its own instance, as the
            // mechanism offers the same skill (184) under six names.
            RegisterTravelersTower();
            RegisterAnomalyPlaces();
            RegisterAlmanaxTemple();
            RegisterGargandiasTemple();

            // The passages between maps. Houses have already gone through above with their jqw protocol;
            // only the generic ones TeleportManager has validated and left active come in here.
            //
            // Giny's rule: every element with a teleport is clickable, be the graphic a sun, a
            // staircase or a door. All are declared alike and resolved by their ElementId.
            foreach (var route in TeleportManager.All)
            {
                if (route.NpcId != 0) continue;
                Declared(route.SourceMapId, new Interactives.Element(route.ElementId, route.SourceCellId, route.GfxId),
                         route.InteractiveType).AddNamed(InteractiveActionKind.Teleport, route.SkillId, route.SkillNameId);
            }

            // The marketplace counters, by element and not by graphic: Brakmar's five share one
            // graphic and each opens another marketplace. Declared with the type of their kind and
            // the skill every real jss gives them; something already declared keeps its own.
            int counters = 0;
            foreach (long mapId in Interactives.MapIds)
            {
                foreach (var (element, house) in Marketplaces.On(mapId))
                {
                    if (_byElement.ContainsKey((mapId, element.Id))) continue;
                    Register(mapId, element, house.InteractiveType, InteractiveActionKind.Marketplace,
                             Marketplaces.Skill);
                    counters++;
                }
            }
            if (counters > 0) Console.WriteLine($"[Marketplaces] {counters} counter declarations on the maps.");

            // And the profession resources, which are by far the most numerous: twenty-five thousand.
            // They are recognised by their graphic just like everything else.
            foreach (long mapId in Interactives.MapIds)
            {
                foreach (var resource in Resources.On(mapId))
                {
                    // An element with a passage was declared above as the passage, and declaring
                    // it again with a resource's type is the incoherent declaration that stops
                    // the start. Resources leaves those out already; this holds should it have
                    // been read before TeleportManager.
                    if (TeleportManager.TryGet(mapId, resource.ElementId, out _)) continue;
                    // Nor a house chest: gfx 682 is a level-90 tree outside, where it is animated,
                    // and the golden chest of Astrub's houses inside, where it is drawn still.
                    if (Houses.IsInterior(mapId) && _byElement.ContainsKey((mapId, resource.ElementId))) continue;
                    Register(mapId, new Interactives.Element(resource.ElementId, resource.Cell,
                                                             resource.Gfx),
                             resource.Type, InteractiveActionKind.Gather, resource.SkillId);
                }
            }

            // And the workshop stations, by their graphic too. One element can offer several
            // skills -- the magus table of Bonta offers three -- and each one is an action of its
            // own, with its own skill instance, the way the jss declares them.
            int stations = 0;
            foreach (long mapId in Interactives.MapIds)
            {
                foreach (var (element, station) in Workshops.On(mapId))
                {
                    // Something already declared there -- a door, a teleport -- keeps its own
                    // declaration: a second one with another type would not be the same element.
                    if (_byElement.ContainsKey((mapId, element.Id))) continue;
                    foreach (int skill in station.Skills)
                        Register(mapId, element, station.Type, InteractiveActionKind.Workshop, skill);
                    stations++;
                }
            }
            if (stations > 0) Console.WriteLine($"[Workshops] {stations} stations declared.");

            // The artisans' book of each workshop: it opens the directory of the workshop's jobs.
            int books = 0;
            foreach (long mapId in Interactives.MapIds)
            {
                foreach (var (element, type, skill) in Workshops.BooksOn(mapId))
                {
                    if (_byElement.ContainsKey((mapId, element.Id))) continue;
                    Register(mapId, element, type, InteractiveActionKind.Workshop, skill);
                    books++;
                }
            }
            if (books > 0) Console.WriteLine($"[Workshops] {books} artisans' books declared.");

            // The official jss of workshop 192937990 declares the eight elements present in the
            // map data, including those for which the server offers no route. Without f11 the
            // client does not attach some drawings (notably the suns) to the map. So all the
            // elements are declared, but without inventing a skill for them: only the
            // providers passed above stay clickable and resolvable by iwo.
            // The guild raids' Luminarium: its sixteen lantern fish (GuildRaidLuminarium).
            foreach (var (boardMap, fish) in GuildRaidLuminarium.Boards())
            {
                foreach (var element in fish)
                {
                    if (_byElement.ContainsKey((boardMap, element.Id))) continue;
                    Register(boardMap, element, GuildRaidLuminarium.FishType, InteractiveActionKind.RaidLantern,
                             GuildRaidLuminarium.FishSkill);
                }
            }

            // And the Cangrancio's four statues, levers worked in the order of its forms.
            if (GuildRaidExecrabe.StatuesOf(Jondo.Unity.World.Content.Raids.Of(Jondo.Unity.World.Content.Raids.Gigalodon)) is { } statues)
            {
                foreach (var (statue, _) in statues.Statues)
                {
                    if (_byElement.ContainsKey((statues.MapId, statue.Id))) continue;
                    Register(statues.MapId, statue, GuildRaidExecrabe.StatueType, InteractiveActionKind.RaidStatue,
                             GuildRaidExecrabe.StatueSkill);
                }
            }

            // And the Santuario's manuscript, a page on each of its zones: "Arrancar una página".
            foreach (var (manuscriptMap, manuscript, _) in GuildRaidSanctuary.Manuscripts(
                         Jondo.Unity.World.Content.Raids.Of(Jondo.Unity.World.Content.Raids.EternalGardens)))
            {
                if (_byElement.ContainsKey((manuscriptMap, manuscript.Id))) continue;
                Declared(manuscriptMap, manuscript, GuildRaidSanctuary.ManuscriptType)
                    .AddNamed(InteractiveActionKind.RaidManuscript, GuildRaidSanctuary.ManuscriptSkill, GuildRaidSanctuary.TearPageName);
            }

            // And the Obra's paper flowers: "Activar la flor de papel", offered only where the
            // viewer's raid has one standing (ConnectionProtocol.Declare).
            if (GuildRaidMonochrome.WorkOf(Jondo.Unity.World.Content.Raids.Of(Jondo.Unity.World.Content.Raids.EternalGardens)) is { } obra)
            {
                foreach (var (flowerMap, flower) in obra.Flowers.Values)
                {
                    if (_byElement.ContainsKey((flowerMap, flower.Id))) continue;
                    Declared(flowerMap, flower, GuildRaidMonochrome.FlowerType)
                        .AddNamed(InteractiveActionKind.RaidFlower, GuildRaidMonochrome.FlowerSkill, GuildRaidMonochrome.ActivateFlower);
                }
            }

            // And the Patio's miniature gardens: their squares and the button that validates each.
            if (GuildRaidEnclave.WorkOf(Jondo.Unity.World.Content.Raids.Of(Jondo.Unity.World.Content.Raids.EternalGardens)) is { } enclave)
            {
                foreach (var board in enclave.Boards.Values)
                {
                    foreach (var square in Interactives.ElementsOf(board.MapId).Where(e => board.Squares.ContainsKey(e.Id)))
                    {
                        if (_byElement.ContainsKey((board.MapId, square.Id))) continue;
                        Register(board.MapId, square, GuildRaidEnclave.SquareType, InteractiveActionKind.RaidGardenSquare,
                                 GuildRaidEnclave.SquareSkill);
                    }
                    if (_byElement.ContainsKey((board.MapId, board.Button.Id))) continue;
                    Declared(board.MapId, board.Button, GuildRaidEnclave.ButtonType)
                        .AddNamed(InteractiveActionKind.RaidGardenButton, GuildRaidEnclave.ButtonSkill, GuildRaidEnclave.Press);
                }
            }

            // And the Reserva's battleship: the Obra's paper boats and its boards' balls.
            if (GuildRaidBelladona.WorkOf(Jondo.Unity.World.Content.Raids.Of(Jondo.Unity.World.Content.Raids.EternalGardens)) is { } reserva)
            {
                foreach (var slot in reserva.Slots)
                {
                    if (_byElement.ContainsKey((slot.MapId, slot.Element.Id))) continue;
                    Declared(slot.MapId, slot.Element, GuildRaidBelladona.BoatType)
                        .AddNamed(InteractiveActionKind.RaidPaperBoat, GuildRaidBelladona.BoatSkill, GuildRaidBelladona.Reveal);
                }
                foreach (var board in reserva.Boards)
                foreach (var ball in Interactives.ElementsOf(board.MapId).Where(e => board.Balls.ContainsKey(e.Id)))
                {
                    if (_byElement.ContainsKey((board.MapId, ball.Id))) continue;
                    Declared(board.MapId, ball, GuildRaidBelladona.BallType)
                        .AddNamed(InteractiveActionKind.RaidCannonBall, GuildRaidBelladona.BallSkill, GuildRaidBelladona.Fire);
                }
            }

            // And Belladona's chessboards in the Reserva: their pieces and their decoys.
            if (GuildRaidEfedra.WorkOf(Jondo.Unity.World.Content.Raids.Of(Jondo.Unity.World.Content.Raids.EternalGardens)) is { } patio)
            {
                foreach (var board in patio.Boards)
                foreach (var piece in Interactives.ElementsOf(board.MapId).Where(e => board.Pieces.ContainsKey(e.Id) || board.Decoys.ContainsKey(e.Id)))
                {
                    if (_byElement.ContainsKey((board.MapId, piece.Id))) continue;
                    Register(board.MapId, piece, GuildRaidEfedra.PieceType, InteractiveActionKind.RaidChessPiece, GuildRaidEfedra.PieceSkill);
                }
            }

            foreach (long mapId in Interactives.MapIds)
            {
                foreach (var element in Interactives.ElementsOf(mapId))
                    RegisterPassive(mapId, element, Interactives.TypeOfGfx(element.Gfx));
            }

            Console.WriteLine($"[Interactives] {_byElement.Count} elements registered.");
        }

        public static IReadOnlyList<RegisteredInteractive> OnMap(long mapId)
            => _byMap.TryGetValue(mapId, out var entries)
                ? entries
                : Array.Empty<RegisteredInteractive>();

        /// <summary>
        /// Resolves a client request. With element and instance present they must match.
        /// The two earlier tolerances are kept: an absent proto3 field may be zero,
        /// and a single zaap can still be used if both fields arrive as zero.
        /// </summary>
        public static bool TryResolveUse(long mapId, int elementId, int skillInstanceId,
                                         out RegisteredInteractive interactive,
                                         out InteractiveAction action)
        {
            interactive = null!;
            action = null!;

            if (elementId != 0)
            {
                if (!_byElement.TryGetValue((mapId, elementId), out interactive)) return false;
                return TryChooseAction(interactive, skillInstanceId, out action);
            }

            if (!_byMap.TryGetValue(mapId, out var entries)) return false;

            if (skillInstanceId != 0)
            {
                foreach (var candidate in entries)
                {
                    foreach (var candidateAction in candidate.Actions)
                    {
                        if (candidateAction.SkillInstanceId != skillInstanceId) continue;
                        if (action != null) return false; // ambiguous instance: no guessing
                        interactive = candidate;
                        action = candidateAction;
                    }
                }
                return action != null;
            }

            // Compatibility with the old zaap fallback for when proto3 omitted the zeros.
            foreach (var candidate in entries)
            {
                foreach (var candidateAction in candidate.Actions)
                {
                    if (candidateAction.Kind != InteractiveActionKind.Zaap) continue;
                    interactive = candidate;
                    action = candidateAction;
                    return true;
                }
            }
            return false;
        }

        private static bool TryChooseAction(RegisteredInteractive interactive, int skillInstanceId,
                                            out InteractiveAction action)
        {
            action = null!;
            if (skillInstanceId == 0 && interactive.Actions.Count == 1)
            {
                action = interactive.Actions[0];
                return true;
            }

            foreach (var candidate in interactive.Actions)
            {
                if (candidate.SkillInstanceId == skillInstanceId)
                {
                    action = candidate;
                    return true;
                }
            }
            return false;
        }

        private static void Register(long mapId, Interactives.Element element, int type,
                                     InteractiveActionKind kind, int skillId, int instance)
        {
            Declared(mapId, element, type).Add(kind, skillId, instance);
        }

        private static void Register(long mapId, Interactives.Element element, int type,
                                     InteractiveActionKind kind, int skillId)
        {
            Declared(mapId, element, type).Add(kind, skillId);
        }

        /// <summary>
        /// The anomalies' places: each entry's exit, and the clocks of their maps, skill 184 and
        /// type -1 as the captures declare them (AnomalyPlaces).
        /// </summary>
        private static void RegisterAnomalyPlaces()
        {
            const int UseSkill = 184;
            int exits = 0, clocks = 0;
            foreach (var place in AnomalyPlaces.All)
            {
                foreach (long map in new[] { place.Entry, place.Room })
                {
                    foreach (var element in Interactives.ElementsOf(map))
                    {
                        if (map == place.Entry && element.Id == place.Exit)
                        {
                            Register(map, element, Jondo.Unity.World.Quests.QuestBinding.DefaultType,
                                     InteractiveActionKind.AnomalyExit, UseSkill);
                            exits++;
                        }
                        else if (element.Gfx == AnomalyPlaces.RightClock || element.Gfx == AnomalyPlaces.WrongClock)
                        {
                            Register(map, element, Jondo.Unity.World.Quests.QuestBinding.DefaultType,
                                     InteractiveActionKind.AnomalyClock, UseSkill);
                            clocks++;
                        }
                    }
                }
            }
            if (exits > 0) Console.WriteLine($"[Anomalies] {exits} place exit(s) and {clocks} clock(s) declared.");
        }

        /// <summary>
        /// The Almanax temple's ways: the sanctuary's door, the halls' doors and ways out, the
        /// rooms' ways out, skill 114 like the graph's passages there (AlmanaxTemple).
        /// </summary>
        private static void RegisterAlmanaxTemple()
        {
            int ways = 0;
            foreach (var (map, element) in AlmanaxTemple.Ways())
            {
                // The ways out are walked onto, type -1 like the graph's; the doors are clicked.
                bool walked = Array.IndexOf(AlmanaxTemple.RoomExitCells, element.Cell) >= 0
                              || element.Cell == AlmanaxTemple.HallExitCell;
                Register(map, element, walked ? -1 : TeleportManager.GenericTeleportType,
                         InteractiveActionKind.AlmanaxWay, TeleportManager.UseSkill);
                ways++;
            }
            if (ways > 0) Console.WriteLine($"[Almanax] {ways} ways of the temple declared.");
        }

        /// <summary>Gargandias's temple's door, grate and stairs, skill 114 (GargandiasHandler).</summary>
        private static void RegisterGargandiasTemple()
        {
            foreach (var (map, cell) in Handlers.GargandiasHandler.Ways())
            {
                foreach (var element in Interactives.ElementsOf(map))
                {
                    if (element.Cell != cell || _byElement.ContainsKey((map, element.Id))) continue;
                    Register(map, element, TeleportManager.GenericTeleportType,
                             InteractiveActionKind.GargandiasWay, TeleportManager.UseSkill);
                }
            }
        }

        private static void RegisterTravelersTower()
        {
            Interactives.Element? Find(long mapId, int id)
            {
                foreach (var element in Interactives.ElementsOf(mapId))
                    if (element.Id == id) return element;
                return null;
            }

            foreach (var room in TravelersTower.Rooms)
            {
                if (Find(room.MapId, room.Teleporter) is not { } teleporter) continue;
                var declared = Declared(room.MapId, teleporter, TravelersTower.TeleporterType);
                int index = 0;
                foreach (var to in TravelersTower.Rooms)
                    declared.Add(InteractiveActionKind.TowerRoom, to.Skill, TravelersTower.InstanceOf(teleporter.Id, index++));
            }

            if (Find(TravelersTower.PortalRoom, TravelersTower.Creator) is { } creator)
            {
                var declared = Declared(TravelersTower.PortalRoom, creator, TravelersTower.CreatorType);
                int index = 0;
                declared.AddNamed(InteractiveActionKind.TowerCreator, TravelersTower.UseSkill, TravelersTower.CloseName,
                                  TravelersTower.InstanceOf(creator.Id, index++));
                foreach (var dimension in TravelersTower.Dimensions.OrderByDescending(d => d.OpenName))
                    declared.AddNamed(InteractiveActionKind.TowerCreator, TravelersTower.UseSkill, dimension.OpenName,
                                      TravelersTower.InstanceOf(creator.Id, index++));
            }
            if (Find(TravelersTower.PortalRoom, TravelersTower.Portal) is { } portal)
            {
                Declared(TravelersTower.PortalRoom, portal, TravelersTower.PortalType)
                    .Add(InteractiveActionKind.TowerPortal, TravelersTower.UseSkill, TravelersTower.InstanceOf(portal.Id, 0));
            }

            foreach (var (mapId, id) in TravelersTower.WaysBack)
            {
                if (Find(mapId, id) is not { } back) continue;
                var declared = Declared(mapId, back, TravelersTower.PortalType);
                declared.Add(InteractiveActionKind.DimensionExit, TravelersTower.StaySkill, TravelersTower.InstanceOf(back.Id, 0));
                declared.Add(InteractiveActionKind.DimensionExit, TravelersTower.LeaveSkill, TravelersTower.InstanceOf(back.Id, 1));
            }

            if (Find(TreasureQuests.Room, TreasureQuests.Box) is { } box)
            {
                var declared = Declared(TreasureQuests.Room, box, TreasureQuests.BoxType);
                int index = 0;
                foreach (var kind in TreasureQuests.Kinds)
                    declared.AddNamed(InteractiveActionKind.TreasureBox, TreasureQuests.UseSkill, kind.SkillName,
                                      TravelersTower.InstanceOf(box.Id, index++));
            }

            // And the room's Pupitre, a workshop of the maps' recipes, and its planning table.
            if (Find(TreasureQuests.Room, TreasureQuests.Desk) is { } desk)
                Declared(TreasureQuests.Room, desk, TreasureQuests.DeskType)
                    .Add(InteractiveActionKind.Workshop, TreasureQuests.MakeMapSkill);
            if (Find(TreasureQuests.Room, TreasureQuests.PlanningTable) is { } table)
                Declared(TreasureQuests.Room, table, TreasureQuests.PlanningTableType)
                    .Add(InteractiveActionKind.PlanningTable, TreasureQuests.LegendarySkill);

            foreach (var dimension in TravelersTower.Dimensions)
            {
                if (Find(TravelersTower.FissureRoom, dimension.Model) is not { } model) continue;
                Declared(TravelersTower.FissureRoom, model, dimension.ModelType)
                    .AddNamed(InteractiveActionKind.FissureModel, TravelersTower.UseSkill, TravelersTower.LocateName,
                              TravelersTower.InstanceOf(model.Id, 0));
            }
        }

        private static RegisteredInteractive Declared(long mapId, Interactives.Element element, int type)
        {
            var key = (mapId, element.Id);
            if (!_byElement.TryGetValue(key, out var interactive))
            {
                interactive = new RegisteredInteractive(mapId, element, type);
                _byElement.Add(key, interactive);
                if (!_byMap.TryGetValue(mapId, out var entries))
                {
                    entries = new List<RegisteredInteractive>();
                    _byMap.Add(mapId, entries);
                }
                entries.Add(interactive);
            }
            else if (interactive.Type != type || interactive.Element.Cell != element.Cell)
            {
                throw new InvalidOperationException(
                    $"Inconsistent declaration of element {element.Id} on map {mapId}.");
            }

            return interactive;
        }

        private static void RegisterPassive(long mapId, Interactives.Element element, int type)
        {
            var key = (mapId, element.Id);
            if (_byElement.ContainsKey(key)) return;

            var interactive = new RegisteredInteractive(mapId, element, type);
            _byElement.Add(key, interactive);
            if (!_byMap.TryGetValue(mapId, out var entries))
            {
                entries = new List<RegisteredInteractive>();
                _byMap.Add(mapId, entries);
            }
            entries.Add(interactive);
        }
    }
}
