using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Each map's interactive elements, and the zaaps.
    ///
    /// An interactive element is what can be clicked on the map: a zaap, a door, a chest.
    /// The client already knows where each one is and with which drawing, because it goes in the map data; what
    /// it expects from the server is to be told which ones exist, with what number, and what skill
    /// they offer. That travels in the jss:
    ///
    ///   f11 { f1: 1, f4 { f1: skill uid, f2: skill }, f5: element, f6: type }
    ///   f15 { f1: state, f2: cell, f3: element }
    ///
    /// The element's number is not invented by us: it is the `m_interactionId` of the client's data,
    /// checked against a real jss of Amakna Castle, where the message's three elements
    /// and the zaap come out with that same number and that same cell.
    ///
    /// For now only the zaaps are declared. For the rest it is known where they are and what drawing they have,
    /// but not which skill each one offers —the element type is not in the client's data,
    /// the server puts it— and declaring a door without knowing what it does leads nowhere.
    /// </summary>
    public static class Interactives
    {
        /// <summary>The zaap's element type, from the client's interactives table.</summary>
        public const int ZaapType = 16;

        /// <summary>The "Utilizar" skill, which is the one a zaap offers.</summary>
        public const int UseSkill = 114;

        /// <summary>
        /// The type of the zaap VESTIGE, which is not the zaap's.
        ///
        /// Drawing 74685 was declared with type 16, the zaap's, and the captures say 359: the
        /// only element with that drawing that appears in a jss carries it all five times, and 16 never
        /// appears for it. The skill is indeed the same 114, and the client answers with its
        /// iwo all the same, so 359 does not make it unclickable: it calls it by its name.
        ///
        /// A vestige is not a switched-off zaap. It is the place where a temporal anomaly appears; see
        /// <see cref="Anomalies"/>.
        /// </summary>
        public const int VestigeType = 359;

        /// <summary>The vestige's drawing.</summary>
        public const int VestigeGfx = 74685;

        /// <summary>
        /// The zaap's drawings, which is what tells it apart from the map's other elements.
        ///
        /// There are two because there are two models: the usual one and the one of the new zones. They are not
        /// written by hand, they come from crossing the 62 maps with a zaap against their elements: 46 carry
        /// the first and 15 the second. The remaining one, the 62nd, is not found this way.
        /// </summary>
        private static readonly int[] ZaapGfx = { 301199, 74685 };

        /// <summary>
        /// Drawings that open the zaap list but that are never arrived at.
        ///
        /// 37493 appears on three maps and on none of the three does the client's zaap table say
        /// there is a zaap. Since <see cref="ZaapOf"/> requires being in that table, the element was not
        /// declared: three zaaps where the player clicked and nothing happened.
        ///
        /// That they are zaaps is said by the captures, not by a guess: the guild hall —map
        /// 99093249, element 540375, cell 227— appears six times with the pair 114 / 16, which is
        /// exactly that of the usual zaap. The other two, the exit of the Jelifica Dimension
        /// and a map of the Osamodas Jungle, carry the same drawing and appear in no
        /// capture; they are recognised by the drawing just like the 106 of 301199, which is how
        /// everything else is identified here.
        ///
        /// They go apart from <see cref="ZaapGfx"/> on purpose: from these one LEAVES, but one does not ARRIVE. Not
        /// being in the zaap table, the travel's destination list never offers them, and so it
        /// should be. It is the same treatment the haven bag zaap already has.
        /// </summary>
        private static readonly int[] DepartureOnlyGfx = { 37493 };

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
        private static readonly Dictionary<int, int> _measuredTypes = new Dictionary<int, int>();
        private static readonly Dictionary<long, Waypoint> _waypoints = new Dictionary<long, Waypoint>();
        private static readonly List<Waypoint> _ordered = new List<Waypoint>();

        /// <summary>Each subzone's level, which is what the zaap list shows per destination.</summary>
        private static readonly Dictionary<int, int> _subAreaLevels = new Dictionary<int, int>();

        /// <summary>Maps whose zaap has to be stated by hand because it is not recognised by the drawing.</summary>
        private static readonly Dictionary<long, int> _overrides = new Dictionary<long, int>();

        public static int MapCount => _byMap.Count;
        public static int WaypointCount => _ordered.Count;
        public static IReadOnlyList<Waypoint> Waypoints => _ordered;
        public static IEnumerable<long> MapIds => _byMap.Keys;

        public static void Initialize()
        {
            _byMap.Clear();
            _measuredTypes.Clear();
            _waypoints.Clear();
            _ordered.Clear();
            _subAreaLevels.Clear();

            LoadWaypoints();
            LoadElements();
            LoadMeasuredTypes();
            LoadSubAreaLevels();
            LoadOverrides();

            int withZaap = 0;
            foreach (var waypoint in _ordered)
            {
                if (ZaapOf(waypoint.MapId).Id != 0) withZaap++;
            }

            int deSalida = 0;
            foreach (long mapId in _byMap.Keys)
            {
                if (DepartureZaapOf(mapId).Id != 0) deSalida++;
            }

            Console.WriteLine($"[Interactives] {_byMap.Count} mapas con elementos, " +
                              $"{_ordered.Count} zaaps ({withZaap} con su elemento localizado), " +
                              $"{deSalida} de salida.");
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
        /// Each subzone's level, from the JSON block SubAreaTemplates stores. It is what the
        /// client draws next to each destination in the zaap list.
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
        /// The zaaps stated by hand. The file also carries a "_comentario" with the reason for each
        /// one, which is skipped for not being a number.
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

        /// <summary>Does this map have a zaap?</summary>
        public static bool HasZaap(long mapId) => _waypoints.ContainsKey(mapId);

        public static Waypoint? WaypointOf(long mapId)
            => _waypoints.TryGetValue(mapId, out var waypoint) ? waypoint : null;

        /// <summary>
        /// The element that is this map's zaap, or an empty one if there is none.
        ///
        /// It is recognised by the drawing: the zaap is always the same. If the map has a zaap according to the
        /// table but none of its elements carries that drawing, none is declared: placing it
        /// on an invented cell leaves the player clicking where there is nothing.
        /// </summary>
        public static Element ZaapOf(long mapId)
            => _waypoints.ContainsKey(mapId) ? ZaapByGfx(mapId) : default;

        /// <summary>
        /// The element of this map that has a zaap drawing, whether the zaap table has it as a zaap or
        /// not. The haven bag maps carry one and are not in that table: they are places one
        /// travels from, not to.
        /// </summary>
        public static Element ZaapByGfx(long mapId)
        {
            // In order: the usual model first, and the one of the new zones after.
            foreach (int gfx in ZaapGfx)
            {
                var element = ElementByGfx(mapId, gfx);
                if (element.Id != 0) return element;
            }
            return default;
        }

        /// <summary>
        /// This map's departure zaap, if it has one. See <see cref="DepartureOnlyGfx"/> for
        /// why putting the drawing in <see cref="ZaapGfx"/> is not enough.
        /// </summary>
        public static Element DepartureZaapOf(long mapId)
        {
            foreach (int gfx in DepartureOnlyGfx)
            {
                var element = ElementByGfx(mapId, gfx);
                if (element.Id != 0) return element;
            }
            return default;
        }

        /// <summary>
        /// Which type this element is declared with: the zaap's or the vestige's.
        ///
        /// Inside a haven bag 74685 is NOT a vestige: it is the departure zaap, with the model
        /// that goes with the theme. It was checked one by one —the five themes that carry it do not
        /// have any element with drawing 301199— so there it is the only zaap there is and
        /// retyping it would leave the player locked in his house.
        /// </summary>
        public static int TypeOfZaap(long mapId, Element element)
            => element.Gfx == VestigeGfx && !Merkasako.IsHavenBag(mapId) ? VestigeType : ZaapType;

        /// <summary>Is this a vestige and not a zaap? See <see cref="TypeOfZaap"/>.</summary>
        public static bool IsVestige(long mapId, Element element)
            => element.Gfx == VestigeGfx && !Merkasako.IsHavenBag(mapId);

        /// <summary>The element of a map that carries a given drawing, if there is one.</summary>
        public static Element ElementByGfx(long mapId, int gfx)
        {
            if (!_byMap.TryGetValue(mapId, out var elements)) return default;
            foreach (var element in elements)
            {
                if (element.Gfx == gfx && element.Cell != 0) return element;
            }
            return default;
        }

        /// <summary>
        /// Type seen in the 3.6 captures for each drawing. The file encodes type -1 with
        /// the unsigned protobuf sentinel ulong.MaxValue; it must never be replaced
        /// by zero, because the exit suns are precisely declared as type -1 by Ankama.
        /// </summary>
        private static void LoadMeasuredTypes()
        {
            string path = Paths.InteractiveTypesJson;
            if (!File.Exists(path)) return;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out int gfx)) continue;
                    if (entry.Value.TryGetProperty("discrepa", out var disputed) &&
                        disputed.ValueKind == JsonValueKind.True) continue;
                    if (!entry.Value.TryGetProperty("tipo", out var type)) continue;

                    if (type.TryGetInt32(out int measured))
                        _measuredTypes[gfx] = measured;
                    else if (type.TryGetUInt64(out ulong unsigned) && unsigned == ulong.MaxValue)
                        _measuredTypes[gfx] = -1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Interactives] No se pudo leer {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        /// <summary>
        /// Declaration type of a drawing that has no server behaviour yet. -1 is the
        /// neutral type measured for exits; it keeps the drawing without inventing an action.
        /// </summary>
        public static int TypeOfGfx(int gfx)
            => _measuredTypes.TryGetValue(gfx, out int type) ? type : -1;

        /// <summary>
        /// The elements of a map that open the zaap list. One at most.
        ///
        /// Almost always it is the one recognised by the drawing. For the maps where that drawing does not
        /// appear —today only the Temple of alliances— the element is stated by hand in
        /// zaap_overrides.json, with the reasoning written inside.
        ///
        /// Before, on those maps ALL the elements were declared as a zaap so that the player would not
        /// be locked in. It worked, but it turned the temple's doors into zaaps, which is
        /// a lie: each element has its own thing and not everything is travelling.
        /// </summary>
        /// <summary>
        /// EVERYTHING on this map, without filtering by what it is.
        ///
        /// The accessors up here look for one concrete thing —the zaap, the haven bag one— and that
        /// works while they are known one by one. The bins and the zaapis are recognised by their
        /// graphic and there are dozens, so they need to look at the whole list and keep theirs.
        /// </summary>
        public static IReadOnlyList<Element> ElementsOf(long mapId)
            => _byMap.TryGetValue(mapId, out var found)
                ? found
                : (IReadOnlyList<Element>)Array.Empty<Element>();

        public static List<Element> ZaapElements(long mapId)
        {
            var salida = new List<Element>();

            var zaap = ZaapOf(mapId);
            if (zaap.Id != 0) { salida.Add(zaap); return salida; }

            // The haven bag one, which is not in the zaap table but is used all the same.
            var propio = Merkasako.ZaapOf(mapId);
            if (propio.Id != 0) { salida.Add(propio); return salida; }

            // And the one of the places one can only leave from, which are not in the zaap table.
            var deSalida = DepartureZaapOf(mapId);
            if (deSalida.Id != 0) { salida.Add(deSalida); return salida; }

            // And the one stated by hand, for those not recognised by the drawing.
            if (_overrides.TryGetValue(mapId, out int elementId))
            {
                var elegido = ByElementId(mapId, elementId);
                if (elegido.Id != 0) salida.Add(elegido);
            }
            return salida;
        }

        /// <summary>
        /// Can this map be left through its zaap? If not, it is not offered as a destination: taking
        /// someone to a place he cannot come back from is worse than not taking him.
        /// </summary>
        public static bool CanLeaveFrom(long mapId) => ZaapElements(mapId).Count > 0;

        /// <summary>
        /// The zaaps the client is told are discovered on entering the world.
        ///
        /// Here the character has them all, so they are all the activated ones for which it is also
        /// known where their element is. Without this list the travel window comes out empty however
        /// many destinations the hjj brings; see ConnectionProtocol.BuildDiscoveredZaaps.
        /// </summary>
        public static IEnumerable<long> DiscoveredZaapMaps()
        {
            foreach (var waypoint in _ordered)
            {
                if (!waypoint.Activated) continue;
                if (!CanLeaveFrom(waypoint.MapId)) continue;
                yield return waypoint.MapId;
            }
        }

        /// <summary>
        /// The skill instance identifier, which is what the client returns on
        /// using the element. The real server hands out numbers with no visible pattern; here it is derived
        /// from the element so that it is stable between sessions and does not have to be stored.
        /// </summary>
        public static int SkillInstanceOf(int elementId) => (elementId % 900000) + 10000;

        /// <summary>The element a skill instance identifier belongs to.</summary>
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
