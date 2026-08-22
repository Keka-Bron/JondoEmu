using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>En qué estado está un recurso. Es el f4 del f15 del jss.</summary>
    public enum ResourceState
    {
        /// <summary>Lleno. El servidor real no manda el campo.</summary>
        Full = 0,

        /// <summary>Agotado: alguien acaba de recogerlo.</summary>
        Depleted = 1,

        /// <summary>Alguien lo está recogiendo ahora mismo.</summary>
        Busy = 2,
    }

    /// <summary>
    /// Los recursos recolectables del mundo: trigo, fresnos, caladeros, minerales.
    ///
    /// ─── Cómo se reconoce un recurso ────────────────────────────────────────────────────────
    ///
    /// El cliente sabe dónde está cada elemento y con qué dibujo, pero no qué es: el TIPO y la
    /// HABILIDAD los pone el servidor. Así que se cruzan las 305 capturas con el volcado del
    /// cliente —lo hace tools/recursos_recoleccion.py— y sale dibujo → (tipo, habilidad). De la
    /// habilidad, el catálogo del cliente da el oficio y qué objeto se saca, porque
    /// <c>gatheredRessourceItem</c> está en skills.json.
    ///
    /// Salen 60 dibujos, que son 25.090 recursos en 4.507 mapas y los seis oficios de recolección.
    /// Minero y Cazador salen flojos —415 y 325— porque las capturas apenas pisaron minas ni
    /// zonas de caza; con más capturas suben solos, sin tocar código.
    ///
    /// ─── Cómo se declara en el jss ──────────────────────────────────────────────────────────
    ///
    /// Con una vuelta de tuerca que hay que respetar o el cliente lo pinta mal:
    ///
    ///   lleno     f11 { f1:1, f2:0, f4 { uid, habilidad }, f5: elemento, f6: tipo }   f15 sin f4
    ///   agotado   f11 { f1:1,       f3 { uid, habilidad }, f5: elemento, f6: tipo }   f15 f4 = 1
    ///   en uso    igual que agotado, pero el f15 lleva f4 = 2
    ///
    /// O sea que la habilidad cambia de campo: va en el 4 cuando se puede usar y en el 3 cuando
    /// no. Comprobado en los 25 fresnos de un mismo mapa, sin una sola excepción.
    ///
    /// El f2 del elemento lleno vale 0 en la madera, el trigo y la salvia, y 1 o 3 en los dos
    /// caladeros. No se ha sabido qué distingue esos valores, así que se manda 0: es lo medido en
    /// tres de los cuatro oficios y el cliente lo pinta bien igual.
    ///
    /// ─── El estado no se guarda ─────────────────────────────────────────────────────────────
    ///
    /// Vive en memoria y es del servidor entero, no de cada jugador: si uno siega un trigo, el de
    /// al lado lo ve segado. Al reiniciar vuelven todos llenos, que es lo mismo que pasaría tras
    /// el tiempo de rebrote.
    /// </summary>
    public static class Resources
    {
        /// <summary>Lo que tarda un recurso en volver a estar lleno.</summary>
        public static readonly TimeSpan Regrowth = TimeSpan.FromMinutes(5);

        /// <summary>Lo que dura el gesto de recoger. Del f3 del iwn: 30 décimas.</summary>
        public const int GatherTenths = 30;

        /// <summary>Un recurso concreto puesto en un mapa.</summary>
        public sealed class Resource
        {
            public long MapId { get; init; }
            public int ElementId { get; init; }
            public int Cell { get; init; }
            public int Gfx { get; init; }
            public int Type { get; init; }
            public int SkillId { get; init; }
            public int JobId { get; init; }
            public int ItemId { get; init; }
            public int LevelMin { get; init; }
        }

        private sealed class Kind
        {
            public int Type;
            public int SkillId;
            public int JobId;
            public int ItemId;
            public int LevelMin;
        }

        private static readonly Dictionary<int, Kind> _byGfx = new();
        private static readonly Dictionary<long, List<Resource>> _byMap = new();
        private static readonly Dictionary<(long, int), Resource> _byElement = new();

        /// <summary>Cuándo vuelve a estar lleno cada recurso agotado. Sin entrada = lleno.</summary>
        private static readonly ConcurrentDictionary<(long, int), DateTime> _spent = new();

        /// <summary>Los que alguien está recogiendo ahora mismo.</summary>
        private static readonly ConcurrentDictionary<(long, int), bool> _busy = new();

        public static int Count => _byElement.Count;
        public static int MapCount => _byMap.Count;

        public static void Initialize()
        {
            _byGfx.Clear();
            _byMap.Clear();
            _byElement.Clear();
            _spent.Clear();
            _busy.Clear();

            string path = Paths.Resolve("recursos_3.6.10.10.json");
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Recursos] Falta {Path.GetFileName(path)}; sin él no hay " +
                                  "recolección. Genéralo con tools/recursos_recoleccion.py.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("recursos", out var list)) return;

                foreach (var entry in list.EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out int gfx)) continue;
                    var v = entry.Value;
                    _byGfx[gfx] = new Kind
                    {
                        Type = v.GetProperty("tipo").GetInt32(),
                        SkillId = v.GetProperty("habilidad").GetInt32(),
                        JobId = v.TryGetProperty("oficio", out var j) ? j.GetInt32() : 0,
                        ItemId = v.TryGetProperty("objeto", out var i) ? i.GetInt32() : 0,
                        LevelMin = v.TryGetProperty("nivel", out var n) ? n.GetInt32() : 1,
                    };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Recursos] No se han podido leer los recursos: {ex.Message}");
                return;
            }

            foreach (long mapId in Interactives.MapIds)
            {
                List<Resource>? here = null;
                foreach (var element in Interactives.ElementsOf(mapId))
                {
                    if (element.Cell == 0) continue;
                    if (!_byGfx.TryGetValue(element.Gfx, out var kind)) continue;

                    var resource = new Resource
                    {
                        MapId = mapId,
                        ElementId = element.Id,
                        Cell = element.Cell,
                        Gfx = element.Gfx,
                        Type = kind.Type,
                        SkillId = kind.SkillId,
                        JobId = kind.JobId,
                        ItemId = kind.ItemId,
                        LevelMin = kind.LevelMin,
                    };

                    var clave = (mapId, element.Id);
                    if (_byElement.ContainsKey(clave)) continue;
                    _byElement.Add(clave, resource);
                    (here ??= new List<Resource>()).Add(resource);
                }
                if (here != null) _byMap.Add(mapId, here);
            }

            var oficios = new SortedDictionary<int, int>();
            foreach (var r in _byElement.Values)
            {
                oficios.TryGetValue(r.JobId, out int n);
                oficios[r.JobId] = n + 1;
            }

            Console.WriteLine($"[Recursos] {_byElement.Count} recolectables en {_byMap.Count} " +
                              $"mapas, {_byGfx.Count} gráficos, {oficios.Count} oficios.");
        }

        public static IReadOnlyList<Resource> On(long mapId)
            => _byMap.TryGetValue(mapId, out var list)
                ? list
                : (IReadOnlyList<Resource>)Array.Empty<Resource>();

        public static bool TryGet(long mapId, int elementId, out Resource resource)
            => _byElement.TryGetValue((mapId, elementId), out resource!);

        public static bool Is(long mapId, int elementId) => _byElement.ContainsKey((mapId, elementId));

        /// <summary>
        /// En qué estado está. Un recurso agotado vuelve solo cuando pasa el rebrote, así que no
        /// hace falta ningún temporizador: se mira la hora cuando alguien pregunta.
        /// </summary>
        public static ResourceState StateOf(long mapId, int elementId)
        {
            var clave = (mapId, elementId);
            if (_busy.ContainsKey(clave)) return ResourceState.Busy;
            if (!_spent.TryGetValue(clave, out var cuando)) return ResourceState.Full;
            if (DateTime.UtcNow >= cuando)
            {
                _spent.TryRemove(clave, out _);
                return ResourceState.Full;
            }
            return ResourceState.Depleted;
        }

        /// <summary>Coge el recurso para recogerlo. Devuelve falso si otro llegó antes.</summary>
        public static bool TryHold(long mapId, int elementId)
        {
            if (StateOf(mapId, elementId) != ResourceState.Full) return false;
            return _busy.TryAdd((mapId, elementId), true);
        }

        /// <summary>Se ha recogido: queda agotado hasta que rebrote.</summary>
        public static void Spend(long mapId, int elementId)
        {
            var clave = (mapId, elementId);
            _spent[clave] = DateTime.UtcNow + Regrowth;
            _busy.TryRemove(clave, out _);
        }

        /// <summary>
        /// ¿Le da el nivel de oficio al jugador que está mirando este mapa?
        ///
        /// Se mira aquí y no sólo al clicar porque el juego real no deja ni intentarlo: al pasar
        /// el ratón por un recurso que te queda grande, el icono sale en rojo igual que si
        /// estuviera agotado. Eso lo hace el cliente solo, con que el servidor declare la
        /// habilidad como no pulsable. Avisar por el chat estaba mal por dos motivos: no es lo
        /// que hace el juego, y esa línea sale por el canal general y la lee todo el mundo.
        /// </summary>
        public static bool WithinReach(long mapId, int elementId)
        {
            if (!_byElement.TryGetValue((mapId, elementId), out var resource)) return true;
            return Network.SessionContext.State.JobLevel(resource.JobId) >= resource.LevelMin;
        }

        /// <summary>Se ha soltado sin recoger —el jugador se fue, o falló algo—.</summary>
        public static void Release(long mapId, int elementId)
            => _busy.TryRemove((mapId, elementId), out _);
    }
}
