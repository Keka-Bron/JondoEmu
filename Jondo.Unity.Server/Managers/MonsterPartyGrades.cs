using System;
using System.Collections.Generic;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Bosses whose grade follows the number of characters that fight them, where a guide says so
    /// (content/monsters/party_grades.json).
    /// </summary>
    /// <remarks>
    /// A dungeon boss comes in at its top grade (<see cref="MobSpawnManager.MembersFor"/>), as the
    /// dungeon capture shows. Some keep one level on every grade and change only their life, and
    /// their guide ties that life to the party: Ilyzaelle has 42 000 life for four characters or
    /// fewer and 60 000 for eight. Nothing in the client says which bosses do, so each is written
    /// from its guide.
    /// </remarks>
    public static class MonsterPartyGrades
    {
        public const string File = "monsters/party_grades.json";

        private static Dictionary<int, int> _from;
        private static readonly object _gate = new object();

        /// <summary>
        /// The grade index this monster fights <paramref name="fighters"/> characters at, or null
        /// when its grade does not follow the party.
        /// </summary>
        public static int? GradeFor(int monster, int fighters, int grades)
        {
            Ensure();
            if (grades <= 0 || !_from.TryGetValue(monster, out int from)) return null;
            return Math.Clamp(fighters - from, 0, grades - 1);
        }

        /// <summary>Reads the file again, for a content reload.</summary>
        public static void Reload()
        {
            lock (_gate) _from = null;
            Ensure();
        }

        private static void Ensure()
        {
            if (_from != null) return;
            lock (_gate)
            {
                if (_from != null) return;
                var rows = new Dictionary<int, int>();
                string path = Paths.ContentFile(File);
                try
                {
                    if (System.IO.File.Exists(path))
                    {
                        foreach (var (monster, from) in Parse(System.IO.File.ReadAllText(path))) rows[monster] = from;
                        Console.WriteLine($"[PartyGrades] {rows.Count} monster(s) graded by party size.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PartyGrades] Could not read {File}: {ex.Message}");
                }
                _from = rows;
            }
        }

        /// <summary>The rows of the file's text; those without a monster or a positive from are left out.</summary>
        public static List<(int Monster, int From)> Parse(string json)
        {
            var rows = new List<(int, int)>();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("grades", out var list) || list.ValueKind != JsonValueKind.Array) return rows;
            foreach (var row in list.EnumerateArray())
            {
                int monster = row.TryGetProperty("monster", out var m) && m.TryGetInt32(out int mv) ? mv : 0;
                int from = row.TryGetProperty("from", out var f) && f.TryGetInt32(out int fv) ? fv : 0;
                if (monster > 0 && from > 0) rows.Add((monster, from));
            }
            return rows;
        }
    }
}
