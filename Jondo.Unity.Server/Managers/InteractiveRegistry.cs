using System;
using System.Collections.Generic;

namespace Jondo.Unity.Launcher.Managers
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
            foreach (long mapId in Interactives.MapIds)
            {
                foreach (var door in Houses.On(mapId))
                    Register(mapId, new Interactives.Element(door.ElementId, door.Cell, door.Gfx),
                             Houses.DoorType, InteractiveActionKind.HouseDoor, Houses.EnterSkill);
            }

            foreach (long interior in Houses.Interiors)
            {
                if (!Houses.TryGetExit(interior, out var exit)) continue;
                Register(interior, new Interactives.Element(exit.ElementId, exit.Cell, exit.Gfx),
                         Houses.ExitType, InteractiveActionKind.HouseExit, Houses.ExitSkill);
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

            // Y los recursos de oficio, que son con diferencia lo mas numeroso: veinticinco mil.
            // Se reconocen por su grafico igual que todo lo demas.
            foreach (long mapId in Interactives.MapIds)
            {
                foreach (var resource in Resources.On(mapId))
                    Register(mapId, new Interactives.Element(resource.ElementId, resource.Cell,
                                                             resource.Gfx),
                             resource.Type, InteractiveActionKind.Gather, resource.SkillId);
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
                                     InteractiveActionKind kind, int skillId)
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

            interactive.Add(kind, skillId);
        }
    }
}
