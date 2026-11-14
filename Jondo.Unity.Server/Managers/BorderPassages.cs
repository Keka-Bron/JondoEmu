using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Map borders that lead somewhere other than the map next door, from the Dofus 2.73 world
    /// graph (datos/border_passages_worldgraph_2.73.json, written by tools/extraer_bordes.py).
    /// </summary>
    /// <remarks>
    /// The client asks to cross a border with its own guess at the map behind it, the map data's
    /// neighbour id, and MapScrolls usually turns that into the real one. Where MapScrolls names a
    /// map the world does not have, the border was a dead end: walking off the right of the
    /// Pandala bridge's first half, 189399809 [16,-24], the client asks for 189268225, which does
    /// not exist, and the character stayed on the bridge. The graph says that border leads to
    /// 189268737 [16,-26], the second half, and that one's right to Pandala, 206307331 [19,-28].
    ///
    /// A passage can hold only for some of the border's cells (the graph's «PC=» criterion: a
    /// border shared by two maps, split by where it is crossed) and only for whoever meets its
    /// criterion. The narrowest one that holds is taken; none holding leaves the border to
    /// MapScrolls, as before.
    /// </remarks>
    public static class BorderPassages
    {
        public sealed record Passage(long Map, string Way, HashSet<int>? Cells, string Criterion,
                                     long ToMap, int ToCell);

        private static Dictionary<(long Map, string Way), List<Passage>>? _byBorder;
        private static readonly object _lock = new();

        /// <summary>
        /// Where crossing <paramref name="way"/> («right», «down», «left», «up») from
        /// <paramref name="cell"/> of <paramref name="map"/> leads, for whoever
        /// <paramref name="meets"/> says meets a criterion: the map, and the cell to land on
        /// (-1 when the graph has none and the usual landing applies).
        /// </summary>
        public static bool TryFind(long map, string way, int cell, Func<string, bool> meets,
                                   out long toMap, out int toCell)
        {
            toMap = 0;
            toCell = -1;
            if (!All().TryGetValue((map, way), out var passages)) return false;
            foreach (var passage in passages)
            {
                if (passage.Cells != null && !passage.Cells.Contains(cell)) continue;
                if (passage.Criterion.Length > 0 && !meets(passage.Criterion)) continue;
                toMap = passage.ToMap;
                toCell = passage.ToCell;
                return true;
            }
            return false;
        }

        /// <summary>
        /// The map past the border, ignoring criteria: for a simulated player picking its way out,
        /// which only needs to know that a map is there.
        /// </summary>
        public static long AnyPast(long map, string way, int cell)
            => TryFind(map, way, cell, _ => true, out long toMap, out _) ? toMap : 0;

        /// <summary>Every passage, by its map and way, the narrowest first.</summary>
        internal static Dictionary<(long Map, string Way), List<Passage>> All()
        {
            lock (_lock)
            {
                if (_byBorder != null) return _byBorder;
                var all = new List<Passage>();
                string path = Paths.WorldGraphBorderPassagesJson;
                try
                {
                    if (File.Exists(path))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(path));
                        foreach (var entry in doc.RootElement.GetProperty("borders").EnumerateArray())
                        {
                            HashSet<int>? cells = null;
                            if (entry.TryGetProperty("cells", out var list) && list.ValueKind == JsonValueKind.Array)
                                cells = list.EnumerateArray().Select(c => c.GetInt32()).ToHashSet();
                            int toCell = entry.TryGetProperty("toCell", out var landing)
                                         && landing.ValueKind == JsonValueKind.Number ? landing.GetInt32() : -1;
                            all.Add(new Passage(entry.GetProperty("map").GetInt64(),
                                                entry.GetProperty("way").GetString() ?? "",
                                                cells,
                                                entry.TryGetProperty("criterion", out var c) ? c.GetString() ?? "" : "",
                                                entry.GetProperty("toMap").GetInt64(),
                                                toCell is >= 0 and < 560 ? toCell : -1));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Borders] {Path.GetFileName(path)} is unreadable: {ex.Message}");
                }
                _byBorder = all
                    .GroupBy(p => (p.Map, p.Way))
                    .ToDictionary(g => g.Key,
                                  g => g.OrderByDescending(p => (p.Cells != null ? 2 : 0) + (p.Criterion.Length > 0 ? 1 : 0))
                                        .ToList());
                return _byBorder;
            }
        }

        /// <summary>For the tests: read the file again.</summary>
        internal static void Forget()
        {
            lock (_lock) _byBorder = null;
        }
    }
}
