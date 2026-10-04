using System;
using System.Collections.Generic;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>La acción de juego que hay detrás de una habilidad interactiva.</summary>
    public enum InteractiveActionKind
    {
        Zaap,
        Chest,
        Lottery,

        /// <summary>El transporte corto dentro de Bonta y Brakmar.</summary>
        Zaapi,

        /// <summary>La papelera: el almacén público de lo que la gente tira.</summary>
        Bin,

        /// <summary>La puerta de la calle de una casa.</summary>
        HouseDoor,

        /// <summary>La puerta de dentro, la que devuelve a la calle.</summary>
        HouseExit,

        /// <summary>Un paso instantáneo entre dos mapas, fuera del sistema de casas.</summary>
        Teleport,

        /// <summary>Un recurso de oficio: trigo, fresno, caladero, mineral.</summary>
        Gather,

        /// <summary>
        /// A workshop station: it opens the craft window, or the smithmagic one when the skill is
        /// a magus'. Both are the same kgq with the skill in it.
        /// </summary>
        Workshop,

        /// <summary>El pozo de los Suenos Infinitos, que abre la ventana del sueno.</summary>
        Dream,

        /// <summary>Una de las tres puertas de una sala, que lleva a la fila de abajo.</summary>
        DreamDoor,

        /// <summary>El altar del Templo de los Gremios, que abre el editor de fundación.</summary>
        GuildFounding,

        /// <summary>A marketplace counter: it opens the marketplace to buy (kdw).</summary>
        Marketplace,

        /// <summary>A chest inside a house: open it (104) or lock it (105).</summary>
        HouseChest,

        /// <summary>The guild chest, in the banks: "Utilizar" (184) opens the guild's.</summary>
        GuildChest,
    }

    /// <summary>Una habilidad ofrecida por un elemento interactivo.</summary>
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
    /// Un elemento interactivo registrado en un mapa, con todas las habilidades que ofrece.
    /// Aunque los tres interactivos actuales solo tienen una, el protocolo admite varias.
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
            // La première action garde exactement l'uid historique. Si un futur élément en offre
            // plusieurs, les suivantes reçoivent les uid contigus encore libres.
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
    /// Registro único de los interactivos que Jondo sabe declarar y ejecutar.
    ///
    /// La clave real es (mapa, elemento); la instancia de habilidad sirve para comprobar la
    /// petición <c>iwo</c>. Los proveedores concretos (zaap, cofre y lotería por ahora) solo se
    /// usan durante <see cref="Initialize"/>. A partir de ahí, la red no necesita conocerlos.
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

            // Este orden conserva exactamente el orden histórico dentro del jss.
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

            // El pozo de los Suenos. Esta en los datos del cliente como un elemento mas -el 539616,
            // grafico 90166, casilla 370 del mapa del Plano Astral- pero sin una accion declarada
            // el cliente ni siquiera deja pulsarlo: es un adorno.
            //
            // El elemento y la habilidad salen del f11 del jss real de ese mapa, no del iwo: el
            // iwo devuelve el UID de instancia, y tomarlo por la habilidad es lo que dejó el pozo
            // sin pulsar. Véase Dreams.HabilidadDelPozo.
            foreach (var pozo in Interactives.ElementsOf(Dreams.MapaDelPozo))
            {
                if (pozo.Id != Dreams.ElementoDelPozo) continue;
                Register(Dreams.MapaDelPozo, pozo, Dreams.TipoDelPozo,
                         InteractiveActionKind.Dream, Dreams.HabilidadDelPozo);
                Register(Dreams.MapaDelPozo, pozo, Dreams.TipoDelPozo,
                         InteractiveActionKind.Dream, Dreams.SegundaHabilidadDelPozo);
            }

            // Y las puertas de las salas. Sin esto pasa lo mismo que pasaba con el pozo: el
            // cliente las dibuja —están en sus propios datos de mapa— y no deja pulsarlas, así
            // que el jugador entra en el sueño, se planta en la sala de entrada y no tiene por
            // dónde seguir. Ningún error, otra vez.
            //
            // Medido en las ocho salas que salen en las capturas, 48 declaraciones y todas
            // iguales: f11 { f1: 1, f4 { uid, 184 }, f5: el elemento, f6: -1 }.
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

            // Los zaapis y las papeleras se reconocen por su GRÁFICO y son decenas, así que se
            // registran en bloque en vez de uno a uno como el zaap o la lotería.
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

            // Las casas van en dos vueltas: las puertas de la calle, que están en mapas del mundo,
            // y las de dentro, que están en interiores que no aparecen en Interactives.MapIds.
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

            // El altar del Templo de los Gremios. Como el pozo: está en los datos del cliente como
            // un elemento más -el 480310, casilla 326 del mapa 106169344- y sin una acción
            // declarada es un adorno. La habilidad y el elemento salen del iwo/iwn de la captura
            // de fundar «Jondo»: iwo {3597, 480310} → iwn {1, 480310, f4 184} y detrás el jjc que
            // abre el editor. Véase GuildHandler.OpenFoundingAsync.
            foreach (var altar in Interactives.ElementsOf(Handlers.GuildHandler.FoundingMap))
            {
                if (altar.Id != Handlers.GuildHandler.FoundingAltar) continue;
                Register(Handlers.GuildHandler.FoundingMap, altar, Handlers.GuildHandler.FoundingType,
                         InteractiveActionKind.GuildFounding, Handlers.GuildHandler.FoundingSkill);
            }

            // Los pasos entre mapas. Las casas ya han pasado por arriba con su protocolo jqw;
            // aquí sólo entran los genéricos que TeleportManager ha validado y dejado activos.
            //
            // Regla de Giny: todo elemento con teleport es clicable, sea el gráfico un sol, una
            // escalera o una puerta. Todos se declaran igual y se resuelven por su ElementId.
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

            // Y los recursos de oficio, que son con diferencia lo mas numeroso: veinticinco mil.
            // Se reconocen por su grafico igual que todo lo demas.
            foreach (long mapId in Interactives.MapIds)
            {
                foreach (var resource in Resources.On(mapId))
                    Register(mapId, new Interactives.Element(resource.ElementId, resource.Cell,
                                                             resource.Gfx),
                             resource.Type, InteractiveActionKind.Gather, resource.SkillId);
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

            // Le jss officiel de l'atelier 192937990 déclare les huit éléments présents dans les
            // données de carte, y compris ceux dont le serveur n'offre aucune route. Sans f11 le
            // client ne rattache pas certains dessins (notamment les soleils) à la carte. On
            // déclare donc tous les éléments, mais sans leur inventer de compétence : seuls les
            // fournisseurs passés ci-dessus restent cliquables et résolubles par iwo.
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
        /// Resuelve una petición del cliente. Con elemento e instancia presentes deben coincidir.
        /// Se conservan las dos tolerancias anteriores: un campo proto3 ausente puede valer cero,
        /// y un zaap único sigue pudiéndose usar si ambos campos llegan a cero.
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
                        if (action != null) return false; // instancia ambigua: no se adivina
                        interactive = candidate;
                        action = candidateAction;
                    }
                }
                return action != null;
            }

            // Compatibilidad con el viejo fallback del zaap cuando proto3 omitía los ceros.
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
