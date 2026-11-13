using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Each dungeon room's board -- the arena its fight is fought on -- and that board's own
    /// placement cells, the ones the game always places the room's fight on.
    /// </summary>
    /// <remarks>
    /// The client marks a map's placement in its cellsData[], its red and blue flags, as it does the
    /// koliseo arenas' (<see cref="KoliseoMaps"/>). A room has twin arenas in its subarea, its
    /// placement a cell or a row off; the board is the twin whose cells share the most with the
    /// room's (datos/mazmorras_colocacion.json, extract_dungeon_placements.py). Measured on the
    /// Royal Gobball's five rooms against the real game's screenshots: each room's own cells are
    /// the real ones, and the subarea's arena MapManager picked by its rule was another room's twin
    /// for four of them -- another room's scenery and placement. And the fight was drawn anew like
    /// any other, where the game keeps a room's placement the same every time.
    ///
    /// <b>Mind the names, they cross over</b>, as in the koliseo: the client's red cells are our
    /// BLUE team (team 0, the kba's f1), its blue ones our red. The file keeps the client's names
    /// and the translation is done here, once.
    /// </remarks>
    public static class DungeonPlacements
    {
        private static readonly Dictionary<long, (long Board, List<int> Blue, List<int> Red)> _rooms = new();
        private static readonly object _lock = new object();
        private static volatile bool _loaded;

        /// <summary>How many rooms have their board and its placement.</summary>
        public static int Count
        {
            get { EnsureLoaded(); return _rooms.Count; }
        }

        /// <summary>Reads the file once, at boot, with its log line.</summary>
        public static void Initialize()
        {
            bool first = !_loaded;
            EnsureLoaded();
            if (first) Console.WriteLine($"[Dungeons] {_rooms.Count} rooms fight on their own board and its placement.");
        }

        /// <summary>The board a dungeon room's fight is fought on, or zero when the file does not say.</summary>
        public static long BoardOf(long roomId)
        {
            EnsureLoaded();
            return _rooms.TryGetValue(roomId, out var room) ? room.Board : 0;
        }

        /// <summary>
        /// A room's blue (players') and red (monsters') cells on its board, or null when the file
        /// does not have the room or the fight is not on its board.
        /// </summary>
        public static (List<int> Blue, List<int> Red)? Of(long roomId, long boardId)
        {
            EnsureLoaded();
            return _rooms.TryGetValue(roomId, out var room) && room.Board == boardId
                ? (new List<int>(room.Blue), new List<int>(room.Red))
                : null;
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                string path = Paths.DungeonPlacementsJson;
                try
                {
                    if (!File.Exists(path))
                    {
                        Console.WriteLine($"[Dungeons] {Path.GetFileName(path)} is missing: dungeon fights are placed " +
                                          "like any other.");
                        return;
                    }

                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (!doc.RootElement.TryGetProperty("mapas", out var mapas)) return;
                    foreach (var entry in mapas.EnumerateArray())
                    {
                        // The client's red ones are our blue, and the other way round.
                        var blue = Read(entry, "rojas");
                        var red = Read(entry, "azules");
                        if (blue.Count > 0 && red.Count > 0)
                            _rooms[entry.GetProperty("sala").GetInt64()] = (entry.GetProperty("id").GetInt64(), blue, red);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Dungeons] Could not read {Path.GetFileName(path)}: {ex.Message}");
                }
                finally
                {
                    _loaded = true;
                }
            }
        }

        private static List<int> Read(JsonElement entry, string field)
        {
            var cells = new List<int>();
            if (entry.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var c in list.EnumerateArray())
                    if (c.TryGetInt32(out int cell)) cells.Add(cell);
            return cells;
        }
    }
}
