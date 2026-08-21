using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// Los elementos interactivos de cada mapa, y los zaaps.
    ///
    /// Un elemento interactivo es lo que se puede clicar en el mapa: un zaap, una puerta, un cofre.
    /// El cliente ya sabe dónde está cada uno y con qué dibujo, porque va en los datos del mapa; lo
    /// que espera del servidor es que le diga cuáles existen, con qué número, y qué habilidad
    /// ofrecen. Eso viaja en el jss:
    ///
    ///   f11 { f1: 1, f4 { f1: uid de la habilidad, f2: habilidad }, f5: elemento, f6: tipo }
    ///   f15 { f1: estado, f2: casilla, f3: elemento }
    ///
    /// El número del elemento no lo inventamos: es el `m_interactionId` de los datos del cliente,
    /// comprobado contra un jss real del Castillo de Amakna, donde los tres elementos del mensaje
    /// y el zaap salen con ese mismo número y esa misma casilla.
    ///
    /// De momento solo se declaran los zaaps. Del resto se sabe dónde están y qué dibujo tienen,
    /// pero no qué habilidad ofrece cada uno —el tipo de elemento no está en los datos del cliente,
    /// lo pone el servidor— y declarar una puerta sin saber qué hace no lleva a ninguna parte.
    /// </summary>
    public static class Interactives
    {
        /// <summary>Tipo de elemento del zaap, de la tabla de interactivos del cliente.</summary>
        public const int ZaapType = 16;

        /// <summary>La habilidad "Utilizar", que es la que ofrece un zaap.</summary>
        public const int UseSkill = 114;

        /// <summary>
        /// The temporal-anomaly vestige type. It shares the newer zaap graphic but is not a
        /// normal waypoint outside a Haven Bag.
        /// </summary>
        public const int VestigeType = 359;

        /// <summary>The graphic used by a temporal-anomaly vestige.</summary>
        public const int VestigeGfx = 74685;

        /// <summary>
        /// Los dibujos del zaap, que es lo que lo distingue del resto de elementos del mapa.
        ///
        /// Son dos porque hay dos modelos: el de siempre y el de las zonas nuevas. No están
        /// escritos a mano, salen de cruzar los 62 mapas con zaap contra sus elementos: 46 llevan
        /// el primero y 15 el segundo. Al que queda, el 62, no se le encuentra por aquí.
        /// </summary>
        private static readonly int[] ZaapGfx = { 301199, 74685 };

        public readonly struct Element
        {
            public Element(int id, int cell, int gfx) { Id = id; Cell = cell; Gfx = gfx; }
            public int Id { get; }
            public int Cell { get; }
            public int Gfx { get; }
        }

        public sealed class Waypoint
        {
            public int Id { get; init; }
            public long MapId { get; init; }
            public int SubAreaId { get; init; }
            public bool Activated { get; init; }
        }

        private static readonly Dictionary<long, List<Element>> _byMap = new Dictionary<long, List<Element>>();
        private static readonly Dictionary<long, Waypoint> _waypoints = new Dictionary<long, Waypoint>();
        private static readonly List<Waypoint> _ordered = new List<Waypoint>();

        /// <summary>El nivel de cada subzona, que es lo que la lista de zaaps enseña por destino.</summary>
        private static readonly Dictionary<int, int> _subAreaLevels = new Dictionary<int, int>();

        /// <summary>Mapas cuyo zaap hay que decir a mano porque no se reconoce por el dibujo.</summary>
        private static readonly Dictionary<long, int> _overrides = new Dictionary<long, int>();

        public static int MapCount => _byMap.Count;
        public static int WaypointCount => _ordered.Count;
        public static IReadOnlyList<Waypoint> Waypoints => _ordered;
        public static IEnumerable<long> MapIds => _byMap.Keys;

        public static void Initialize()
        {
            _byMap.Clear();
            _waypoints.Clear();
            _ordered.Clear();
            _subAreaLevels.Clear();

            LoadWaypoints();
            LoadElements();
            LoadSubAreaLevels();
            LoadOverrides();

            int withZaap = 0;
            foreach (var waypoint in _ordered)
            {
                if (ZaapOf(waypoint.MapId).Id != 0) withZaap++;
            }

            Console.WriteLine($"[Interactives] {_byMap.Count} mapas con elementos, " +
                              $"{_ordered.Count} zaaps ({withZaap} con su elemento localizado).");
        }

        private static void LoadWaypoints()
        {
            string path = Paths.WaypointsJson;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Interactives] Falta {Path.GetFileName(path)}; sin él no hay zaaps. " +
                                  "Genéralo con tools/extract_interactivos.py.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var entry in doc.RootElement.EnumerateArray())
                {
                    var waypoint = new Waypoint
                    {
                        Id = entry.GetProperty("id").GetInt32(),
                        MapId = entry.GetProperty("mapId").GetInt64(),
                        SubAreaId = entry.GetProperty("subAreaId").GetInt32(),
                        Activated = entry.TryGetProperty("activated", out var on) && on.GetInt32() != 0,
                    };
                    if (waypoint.MapId == 0) continue;

                    _waypoints[waypoint.MapId] = waypoint;
                    _ordered.Add(waypoint);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Interactives] No se pudo leer {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        private static void LoadElements()
        {
            string path = Paths.InteractiveElementsJson;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Interactives] Falta {Path.GetFileName(path)}; los zaaps no se " +
                                  "podrán colocar en su casilla.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var map in doc.RootElement.EnumerateObject())
                {
                    if (!long.TryParse(map.Name, out long mapId)) continue;

                    var elements = new List<Element>();
                    foreach (var element in map.Value.EnumerateArray())
                    {
                        elements.Add(new Element(
                            element.TryGetProperty("e", out var id) ? id.GetInt32() : 0,
                            element.TryGetProperty("c", out var cell) ? cell.GetInt32() : 0,
                            element.TryGetProperty("g", out var gfx) ? gfx.GetInt32() : 0));
                    }
                    if (elements.Count > 0) _byMap[mapId] = elements;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Interactives] No se pudo leer {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        /// <summary>
        /// El nivel de cada subzona, del bloque JSON que guarda SubAreaTemplates. Es lo que el
        /// cliente pinta al lado de cada destino en la lista del zaap.
        /// </summary>
        private static void LoadSubAreaLevels()
        {
            try
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                    DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT Id, Data FROM SubAreaTemplates;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(1)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(reader.GetString(1));
                        if (doc.RootElement.TryGetProperty("level", out var level) &&
                            level.TryGetInt32(out int value))
                        {
                            _subAreaLevels[reader.GetInt32(0)] = value;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Interactives] No se pudieron leer los niveles de subzona: {ex.Message}");
            }
        }

        /// <summary>
        /// Los zaaps dichos a mano. El fichero lleva además un "_comentario" con el porqué de cada
        /// uno, que se salta por no ser un número.
        /// </summary>
        private static void LoadOverrides()
        {
            string path = Paths.ZaapOverridesJson;
            if (!File.Exists(path)) return;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    if (!long.TryParse(entry.Name, out long mapId)) continue;
                    if (entry.Value.TryGetInt32(out int elementId)) _overrides[mapId] = elementId;
                }
                if (_overrides.Count > 0)
                {
                    Console.WriteLine($"[Interactives] {_overrides.Count} zaap(s) dichos a mano.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Interactives] No se pudo leer {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        public static int LevelOfSubArea(int subAreaId)
            => _subAreaLevels.TryGetValue(subAreaId, out int level) ? level : 0;

        /// <summary>¿Este mapa tiene zaap?</summary>
        public static bool HasZaap(long mapId) => _waypoints.ContainsKey(mapId);

        public static Waypoint? WaypointOf(long mapId)
            => _waypoints.TryGetValue(mapId, out var waypoint) ? waypoint : null;

        /// <summary>
        /// El elemento que es el zaap de este mapa, o uno vacío si no lo hay.
        ///
        /// Se reconoce por el dibujo: el zaap es siempre el mismo. Si el mapa tiene zaap según la
        /// tabla pero ninguno de sus elementos lleva ese dibujo, no se declara ninguno: colocarlo
        /// en una casilla inventada deja al jugador clicando donde no hay nada.
        /// </summary>
        public static Element ZaapOf(long mapId)
            => _waypoints.ContainsKey(mapId) ? ZaapByGfx(mapId) : default;

        /// <summary>
        /// El elemento de este mapa que tiene dibujo de zaap, lo tenga la tabla de zaaps por zaap o
        /// no. Los mapas del merkasako llevan uno y no están en esa tabla: son sitios desde los que
        /// se viaja, no a los que se viaja.
        /// </summary>
        public static Element ZaapByGfx(long mapId)
        {
            // Por orden: el modelo de siempre primero, y el de las zonas nuevas después.
            foreach (int gfx in ZaapGfx)
            {
                var element = ElementByGfx(mapId, gfx);
                if (element.Id != 0) return element;
            }
            return default;
        }

        /// <summary>Returns the protocol interactive type for a zaap-like element.</summary>
        public static int TypeOfZaap(long mapId, Element element)
            => element.Gfx == VestigeGfx && !Merkasako.IsHavenBag(mapId) ? VestigeType : ZaapType;

        /// <summary>Whether this element opens the anomaly-only list rather than ordinary zaaps.</summary>
        public static bool IsVestige(long mapId, Element element)
            => element.Gfx == VestigeGfx && !Merkasako.IsHavenBag(mapId);

        /// <summary>El elemento de un mapa que lleva un dibujo dado, si es que lo hay.</summary>
        public static Element ElementByGfx(long mapId, int gfx)
        {
            if (!_byMap.TryGetValue(mapId, out var elements)) return default;
            foreach (var element in elements)
            {
                if (element.Gfx == gfx && element.Cell != 0) return element;
            }
            return default;
        }

        /// <summary>All map elements, used by interactive families recognised by graphic.</summary>
        public static IReadOnlyList<Element> ElementsOf(long mapId)
            => _byMap.TryGetValue(mapId, out var found)
                ? found
                : (IReadOnlyList<Element>)Array.Empty<Element>();

        /// <summary>
        /// Los elementos de un mapa que abren la lista de zaaps. Uno como mucho.
        ///
        /// Casi siempre es el que se reconoce por el dibujo. Para los mapas donde ese dibujo no
        /// aparece —hoy solo el Templo de las alianzas— el elemento se dice a mano en
        /// zaap_overrides.json, con el razonamiento escrito dentro.
        ///
        /// Antes, en esos mapas se declaraban TODOS los elementos como zaap para que el jugador no
        /// se quedara encerrado. Funcionaba, pero convertía las puertas del templo en zaaps, que es
        /// mentira: cada elemento tiene lo suyo y no todo es viajar.
        /// </summary>
        public static List<Element> ZaapElements(long mapId)
        {
            var salida = new List<Element>();

            var zaap = ZaapOf(mapId);
            if (zaap.Id != 0) { salida.Add(zaap); return salida; }

            // El del merkasako, que no está en la tabla de zaaps pero se usa igual.
            var propio = Merkasako.ZaapOf(mapId);
            if (propio.Id != 0) { salida.Add(propio); return salida; }

            // Y el dicho a mano, para los que no se reconocen por el dibujo.
            if (_overrides.TryGetValue(mapId, out int elementId))
            {
                var elegido = ByElementId(mapId, elementId);
                if (elegido.Id != 0) salida.Add(elegido);
            }
            return salida;
        }

        /// <summary>
        /// ¿Se puede salir de este mapa por su zaap? Si no, no se ofrece como destino: llevar a
        /// alguien a un sitio del que no puede volver es peor que no llevarlo.
        /// </summary>
        public static bool CanLeaveFrom(long mapId) => ZaapElements(mapId).Count > 0;

        /// <summary>All activated waypoints the emulator exposes as discovered to every character.</summary>
        public static IEnumerable<long> DiscoveredZaapMaps()
        {
            foreach (var waypoint in _ordered)
            {
                if (waypoint.Activated && CanLeaveFrom(waypoint.MapId))
                    yield return waypoint.MapId;
            }
        }

        /// <summary>
        /// El identificador de la instancia de habilidad, que es lo que el cliente devuelve al
        /// usar el elemento. El servidor real reparte números sin patrón visible; aquí se deriva
        /// del elemento para que sea estable entre sesiones y no haya que guardarlo.
        /// </summary>
        public static int SkillInstanceOf(int elementId) => (elementId % 900000) + 10000;

        /// <summary>El elemento al que pertenece un identificador de instancia de habilidad.</summary>
        public static Element ByElementId(long mapId, int elementId)
        {
            if (!_byMap.TryGetValue(mapId, out var elements)) return default;
            foreach (var element in elements)
            {
                if (element.Id == elementId) return element;
            }
            return default;
        }
    }
}
