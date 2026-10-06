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

        /// <summary>A marketplace counter: it opens the marketplace to buy (kdw).</summary>
        Marketplace,

        /// <summary>A chest inside a house: open it (104) or lock it (105).</summary>
        HouseChest,

        /// <summary>The guild chest, in the banks: "Utilizar" (184) opens the guild's.</summary>
        GuildChest,
    }

    /// <summary>A skill offered by an interactive element.</summary>
    public sealed class InteractiveAction
    {
        internal InteractiveAction(InteractiveActionKind kind, int skillId, int skillInstanceId)
        {
            Kind = kind;
            SkillId = skillId;
            SkillInstanceId = skillInstanceId;
        }

        public InteractiveActionKind Kind { get; }
        public int SkillId { get; }
        public int SkillInstanceId { get; }
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

        internal void Add(InteractiveActionKind kind, int skillId)
        {
            // The first action keeps exactly the historical uid. If a future element offers
            // several, the following ones get the contiguous uids still free.
            int instance = Interactives.SkillInstanceOf(Element.Id);
            while (ContainsInstance(instance)) instance++;
            _actions.Add(new InteractiveAction(kind, skillId, instance));
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
        private static readonly Dictionary<long, List<RegisteredInteractive>> _byMap =
            new Dictionary<long, List<RegisteredInteractive>>();
        private static readonly Dictionary<(long MapId, int ElementId), RegisteredInteractive> _byElement =
            new Dictionary<(long, int), RegisteredInteractive>();

        public static int Count => _byElement.Count;

        public static void Initialize()
        {
            _byMap.Clear();
            _byElement.Clear();

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

            if (puertas > 0) Console.WriteLine($"[Sueños] {puertas} puerta(s) de sala declaradas.");

            // The zaapis and the bins are recognised by their GRAPHIC and there are dozens, so they
            // are registered in bulk instead of one by one like the zaap or the lottery.
            foreach (long mapId in Interactives.MapIds)
            {
                foreach (var element in Zaapis.ElementsOn(mapId))
                    Register(mapId, element, Zaapis.Type, InteractiveActionKind.Zaapi, Zaapis.UseSkill);
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

            // The passages between maps. Houses have already gone through above with their jqw protocol;
            // only the generic ones TeleportManager has validated and left active come in here.
            //
            // Giny's rule: every element with a teleport is clickable, be the graphic a sun, a
            // staircase or a door. All are declared alike and resolved by their ElementId.
            foreach (var route in TeleportManager.All)
            {
                Register(route.SourceMapId,
                         new Interactives.Element(route.ElementId, route.SourceCellId, route.GfxId),
                         route.InteractiveType, InteractiveActionKind.Teleport, route.SkillId);
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
            foreach (long mapId in Interactives.MapIds)
            {
                foreach (var element in Interactives.ElementsOf(mapId))
                    RegisterPassive(mapId, element, Interactives.TypeOfGfx(element.Gfx));
            }

            Console.WriteLine($"[Interactives] {_byElement.Count} elementos registrados.");
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
                    $"Declaracion incoherente del elemento {element.Id} en el mapa {mapId}.");
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
