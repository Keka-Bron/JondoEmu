using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The placement a map flags for itself: the red and blue flags of the client's cellsData[], on
    /// the 3,885 maps that carry them (datos/map_placements_3.6.10.11.json, extract_map_placements.py).
    /// </summary>
    /// <remarks>
    /// Most fights are placed by the server, drawn anew each time (PlacementLayout). A board a fight
    /// is sent to on purpose -- a quest's arena -- is placed as the map marks it: the Laberinto de
    /// las almas (136317440) flags the maze's one way in for the soulless mummy and the corner the
    /// age-old minogolem stands in, the other three corners being where its spell calls the rest.
    ///
    /// <b>The names cross over</b>, as in <see cref="DungeonPlacements"/>: the client's red cells
    /// are our BLUE team (the players'), its blue ones our red.
    /// </remarks>
    public static class MapPlacements
    {
        private static readonly Dictionary<long, (List<int> Blue, List<int> Red)> _maps = new();
        private static readonly object _lock = new object();
        private static volatile bool _loaded;

        /// <summary>How many maps flag their placement.</summary>
        public static int Count
        {
            get { EnsureLoaded(); return _maps.Count; }
        }

        /// <summary>
        /// A map's blue (players') and red (monsters') cells, or null when it flags none for one of
        /// the two sides.
        /// </summary>
        public static (List<int> Blue, List<int> Red)? Of(long mapId)
        {
            EnsureLoaded();
            return _maps.TryGetValue(mapId, out var map) ? (new List<int>(map.Blue), new List<int>(map.Red)) : null;
        }

        /// <summary>
        /// A map's own placement when both sides fit on it: <paramref name="players"/> on its blue
        /// cells and <paramref name="monsters"/> on its red ones, or null.
        /// </summary>
        public static (List<int> Blue, List<int> Red)? Fitting(long mapId, int players, int monsters)
            => Of(mapId) is { } own && own.Blue.Count >= Math.Max(1, players) && own.Red.Count >= Math.Max(1, monsters)
                ? own
                : null;

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                string path = Paths.MapPlacementsJson;
                try
                {
                    if (!File.Exists(path))
                    {
                        Console.WriteLine($"[Fight] {Path.GetFileName(path)} is missing: the arenas are placed like any other map.");
                        return;
                    }
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (!doc.RootElement.TryGetProperty("maps", out var maps)) return;
                    foreach (var map in maps.EnumerateObject())
                    {
                        if (!long.TryParse(map.Name, out long mapId)) continue;
                        // The client's red ones are our blue, and the other way round.
                        var blue = Read(map.Value, "red");
                        var red = Read(map.Value, "blue");
                        if (blue.Count > 0 && red.Count > 0) _maps[mapId] = (blue, red);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Fight] Could not read {Path.GetFileName(path)}: {ex.Message}");
                }
                finally
                {
                    _loaded = true;
                }
            }
        }

        private static List<int> Read(JsonElement map, string field)
        {
            var cells = new List<int>();
            if (map.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var c in list.EnumerateArray())
                    if (c.TryGetInt32(out int cell)) cells.Add(cell);
            return cells;
        }
    }
}
