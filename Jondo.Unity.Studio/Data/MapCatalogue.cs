using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Client;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Studio.Data
{
    /// <summary>One map, and where in the world it is.</summary>
    public sealed class MapPlace
    {
        public long MapId { get; init; }

        public int X { get; init; }

        public int Y { get; init; }

        public int SubAreaId { get; init; }

        /// <summary>The area's name in the language in use, when it could be resolved.</summary>
        public string Area { get; init; } = "";

        public bool Outdoor { get; init; }

        /// <summary>How many NPCs and monster groups are on it. What makes a map worth opening.</summary>
        public int Npcs { get; set; }

        public int Groups { get; set; }

        public string Where => $"[{X}, {Y}]";

        public override string ToString()
            => Area.Length > 0 ? $"{Where}  {Area}" : Where;
    }

    /// <summary>
    /// Every map, by id and by where it is.
    /// </summary>
    /// <remarks>
    /// Searching by map id only was the honest first version and a bad one: a map id is a number
    /// nobody carries in their head, and the thing people do know is the coordinate in the corner
    /// of the screen. <c>world.db</c> has both — 15,360 maps in <c>MapPositions</c>, 12,003 of them
    /// with a real coordinate — so "everything at [4, -18]" is one query away.
    ///
    /// More than one map shares a coordinate, which is the whole reason this offers a list rather
    /// than jumping: the square you stand on outdoors and the inside of the house on it are two
    /// different maps at the same [x, y].
    /// </remarks>
    public sealed class MapCatalogue : IDisposable
    {
        private readonly SqliteConnection? _world;
        private readonly Dictionary<int, string> _areas = new Dictionary<int, string>();
        private readonly Dictionary<long, MapPlace> _byId = new Dictionary<long, MapPlace>();
        private readonly IReadOnlyDictionary<long, int>? _npcsPerMap;
        private List<MapPlace>? _all;

        /// <param name="npcsPerMap">
        /// How many NPCs stand on each map, worked out by the caller from the content layers.
        /// </param>
        /// <remarks>
        /// The count comes from outside because the <c>NpcSpawns</c> table in <c>world.db</c> is
        /// almost empty — two maps have a row in it. The world's 422 placements live in
        /// <c>datos/npcs_reales.json</c> and in <c>content/</c>, which is where the merged store
        /// this is handed already has them. Counting the table instead would have shown "0 NPC"
        /// next to every map in the world and looked like a fact.
        /// </remarks>
        public MapCatalogue(ClientText? text = null, Action<string>? report = null,
                            IReadOnlyDictionary<long, int>? npcsPerMap = null)
        {
            _npcsPerMap = npcsPerMap;

            try
            {
                _world = new SqliteConnection(Paths.WorldConnectionString + ";Mode=ReadOnly");
                _world.Open();
                ReadAreas(text);
            }
            catch (Exception ex)
            {
                report?.Invoke($"world.db could not be opened: {ex.Message}");
                _world = null;
            }
        }

        public bool Ready => _world != null;

        private void ReadAreas(ClientText? text)
        {
            if (_world == null) return;

            using var command = _world.CreateCommand();
            command.CommandText = "SELECT Id, Data FROM SubAreaTemplates;";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(1)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(reader.GetString(1));
                    if (!doc.RootElement.TryGetProperty("nameId", out var nameId)) continue;
                    if (!nameId.TryGetInt64(out long key) || key == 0) continue;

                    string name = text?.Of(key) ?? "";
                    if (name.Length > 0) _areas[reader.GetInt32(0)] = name;
                }
                catch (JsonException)
                {
                    // One area without a name is one row reading a bit worse.
                }
            }
        }

        /// <summary>Every map that has a place in the world, loaded once.</summary>
        public List<MapPlace> All()
        {
            if (_all != null) return _all;

            _all = new List<MapPlace>();
            if (_world == null) return _all;

            // Counted in one pass each rather than per map.
            //
            // This was two correlated subqueries inside the map query, and it took SIXTY-SEVEN
            // SECONDS: neither NpcSpawns nor MapMobs is indexed by MapId, so each of the 15,360
            // maps drove two full table scans. It looked exactly like a hang, because it was one —
            // clicking a placement, or typing a bracket into the map field, froze the editor.
            //
            // Grouped up front it is 0.03 seconds, and the database is not ours to add an index to:
            // world.db is regenerated by tooling and any index we added would be gone next time.
            var groups = Counted("SELECT MapId, COUNT(*) FROM MapMobs GROUP BY MapId;");

            using var command = _world.CreateCommand();
            command.CommandText = "SELECT MapId, PosX, PosY, SubAreaId, Outdoor FROM MapPositions;";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                int subArea = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                long mapId = reader.GetInt64(0);

                var place = new MapPlace
                {
                    MapId = mapId,
                    X = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                    Y = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                    SubAreaId = subArea,
                    Area = _areas.TryGetValue(subArea, out string? name) ? name : "",
                    Outdoor = !reader.IsDBNull(4) && reader.GetInt32(4) != 0,
                    Npcs = _npcsPerMap != null && _npcsPerMap.TryGetValue(mapId, out int npcs) ? npcs : 0,
                    Groups = groups.TryGetValue(mapId, out int here) ? here : 0,
                };

                _all.Add(place);
                _byId[mapId] = place;
            }

            _all.Sort((a, b) =>
            {
                int byX = a.X.CompareTo(b.X);
                if (byX != 0) return byX;
                int byY = a.Y.CompareTo(b.Y);
                return byY != 0 ? byY : a.MapId.CompareTo(b.MapId);
            });

            return _all;
        }

        /// <summary>The four maps around one, as (top, right, bottom, left). Zero where there is none.</summary>
        /// <remarks>
        /// Because this is how the world is actually walked. Hunting for the map next door by its
        /// eight-digit id, when the game itself just lets you walk off the edge, is the kind of
        /// friction that stops somebody editing a whole area in one sitting.
        /// </remarks>
        public (long Top, long Right, long Bottom, long Left) Around(long mapId)
        {
            Neighbours();
            return _around.TryGetValue(mapId, out var four) ? four : (0, 0, 0, 0);
        }

        private Dictionary<long, (long, long, long, long)>? _neighbours;

        private Dictionary<long, (long, long, long, long)> Neighbours()
        {
            if (_neighbours != null) return _neighbours;

            _neighbours = new Dictionary<long, (long, long, long, long)>();
            string path = Paths.MapNeighboursJson;
            if (!File.Exists(path)) return _neighbours;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    if (!long.TryParse(entry.Name, out long mapId)) continue;

                    _neighbours[mapId] = (Side(entry.Value, "top"), Side(entry.Value, "right"),
                                          Side(entry.Value, "bottom"), Side(entry.Value, "left"));
                }
            }
            catch (JsonException)
            {
                // No neighbours is four buttons greyed out, not a lost screen.
            }

            return _neighbours;
        }

        private Dictionary<long, (long Top, long Right, long Bottom, long Left)> _around
            => Neighbours();

        private static long Side(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.TryGetInt64(out long id) && id > 0
                ? id : 0;

        public MapPlace? Of(long mapId)
        {
            All();
            return _byId.TryGetValue(mapId, out var place) ? place : null;
        }

        /// <summary>One column of counts, in one pass.</summary>
        private Dictionary<long, int> Counted(string sql)
        {
            var counts = new Dictionary<long, int>();
            if (_world == null) return counts;

            using var command = _world.CreateCommand();
            command.CommandText = sql;

            using var reader = command.ExecuteReader();
            while (reader.Read()) counts[reader.GetInt64(0)] = reader.GetInt32(1);
            return counts;
        }

        /// <summary>
        /// Finds maps from what somebody typed: a map id, a coordinate, or part of an area's name.
        /// </summary>
        /// <remarks>
        /// One field for all three because they are not ambiguous in practice: a coordinate has a
        /// separator in it, a map id is eight digits, and a name has letters. Making somebody
        /// choose the kind of search first would be one click for nothing.
        /// </remarks>
        public List<MapPlace> Find(string? needle, int most = 200)
        {
            var found = new List<MapPlace>();
            needle = (needle ?? "").Trim();
            if (needle.Length == 0) return found;

            if (TryCoordinates(needle, out int x, out int y))
            {
                foreach (var place in All())
                {
                    if (place.X == x && place.Y == y) found.Add(place);
                    if (found.Count >= most) break;
                }

                return found;
            }

            bool numeric = long.TryParse(needle, out long asNumber);
            foreach (var place in All())
            {
                bool hit = (numeric && place.MapId == asNumber)
                        || place.MapId.ToString().Contains(needle, StringComparison.Ordinal)
                        || (place.Area.Length > 0 &&
                            place.Area.Contains(needle, StringComparison.CurrentCultureIgnoreCase));

                if (!hit) continue;

                found.Add(place);
                if (found.Count >= most) break;
            }

            return found;
        }

        /// <summary>"4,-18", "4 -18" and "[4, -18]" all mean the same square.</summary>
        public static bool TryCoordinates(string text, out int x, out int y)
        {
            x = y = 0;

            string cleaned = text.Replace('[', ' ').Replace(']', ' ')
                                 .Replace(',', ' ').Replace(';', ' ').Replace('/', ' ')
                                 .Replace('|', ' ').Trim();

            string[] parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return false;

            return int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y);
        }

        public void Dispose() => _world?.Dispose();
    }
}
