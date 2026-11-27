using System;
using System.Collections.Generic;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// How far from the nearest enemy a ranged monster ends its turn, where its kit or a guide says
    /// so (content/monsters/standoffs.json).
    /// </summary>
    /// <remarks>
    /// A ranged monster ends its turn at the reach of its best blow from the nearest enemy
    /// (<see cref="MonsterTactics.Reposition"/>). Some are built to stay close all the same: the
    /// library yokais of «En la encrucijada de los mundos» (2150) hit from ten cells, but their kit
    /// pushes from exactly two and the fight is only won by reaching all five in one turn. Kept at
    /// ten cells, out of sight, with ten MP each, they could never be reached. Each row is the
    /// distance such a monster keeps.
    /// </remarks>
    public static class MonsterStandoffs
    {
        public const string File = "monsters/standoffs.json";

        private static Dictionary<int, int> _cells;
        private static readonly object _gate = new object();

        /// <summary>The distance this monster template keeps from the nearest enemy; 0 when it keeps its blow's reach.</summary>
        public static int For(int monster)
        {
            if (monster <= 0) return 0;
            Ensure();
            return _cells.TryGetValue(monster, out int cells) ? cells : 0;
        }

        /// <summary>Reads the file again, for a content reload.</summary>
        public static void Reload()
        {
            lock (_gate) _cells = null;
            Ensure();
        }

        private static void Ensure()
        {
            if (_cells != null) return;
            lock (_gate)
            {
                if (_cells != null) return;
                var cells = new Dictionary<int, int>();
                string path = Paths.ContentFile(File);
                try
                {
                    if (System.IO.File.Exists(path))
                    {
                        foreach (var (monster, distance) in Parse(System.IO.File.ReadAllText(path))) cells[monster] = distance;
                        Console.WriteLine($"[Standoffs] {cells.Count} monster(s) with a standoff distance.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Standoffs] Could not read {File}: {ex.Message}");
                }
                _cells = cells;
            }
        }

        /// <summary>The rows of the file's text; those without a monster or a positive distance are left out.</summary>
        public static List<(int Monster, int Cells)> Parse(string json)
        {
            var rows = new List<(int, int)>();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("standoffs", out var list) || list.ValueKind != JsonValueKind.Array) return rows;
            foreach (var row in list.EnumerateArray())
            {
                int monster = row.TryGetProperty("monster", out var m) && m.TryGetInt32(out int mv) ? mv : 0;
                int distance = row.TryGetProperty("cells", out var c) && c.TryGetInt32(out int cv) ? cv : 0;
                if (monster > 0 && distance > 0) rows.Add((monster, distance));
            }
            return rows;
        }
    }
}
