using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The client's legendary treasure hunts, as its own data has them (datos/legendary_hunts_*.json,
    /// extract_legendary_hunts.py): the level, the monster that waits at the end, the chest it
    /// gives and the map item that starts it.
    /// </summary>
    public static class LegendaryHunts
    {
        /// <summary>One legendary hunt: «Tesoro de Anutropía» is 58, level 100, its fissure 8173, its chest 32509.</summary>
        public sealed record Definition(int Id, int NameId, int Level, int Monster, int Chest, int MapItem, double XpRatio);

        private static Dictionary<int, Definition>? _hunts;
        private static readonly object _lock = new();

        private static Dictionary<int, Definition> Loaded
        {
            get
            {
                if (_hunts != null) return _hunts;
                lock (_lock)
                {
                    _hunts ??= Load(Paths.LegendaryHuntsJson);
                    return _hunts;
                }
            }
        }

        /// <summary>Every legendary hunt the client has.</summary>
        public static IEnumerable<Definition> All => Loaded.Values;

        /// <summary>The legendary hunt with that id, or null when the client does not have it.</summary>
        public static Definition? Of(int id) => Loaded.TryGetValue(id, out var found) ? found : null;

        /// <summary>The legendary hunt whose final monster this is, or null.</summary>
        public static Definition? EndingWith(int monster)
        {
            foreach (var hunt in Loaded.Values)
                if (hunt.Monster == monster) return hunt;
            return null;
        }

        /// <summary>The company written by hand, relative to the content root.</summary>
        public const string CompanyFile = "quests/legendary_company.json";

        private static Dictionary<int, int[]>? _company;

        /// <summary>
        /// The monsters that wait with a criminal hunt's own at its end, beyond the one the client's
        /// row names: «Trésor des bandits de Cania» (57) names Nomekop alone, and «Un combat se lance
        /// contre les trois bandits de Cania» (Dofus pour les noobs) -- Eratz and Edass with him.
        /// </summary>
        public static IReadOnlyList<int> CompanyOf(int hunt)
        {
            var company = _company;
            if (company == null)
            {
                lock (_lock) company = _company ??= LoadCompany(Paths.ContentFile(CompanyFile));
            }
            return company.TryGetValue(hunt, out var monsters) ? monsters : Array.Empty<int>();
        }

        /// <summary>For the harness's reload: read the company again.</summary>
        internal static void ForgetCompany()
        {
            lock (_lock) _company = null;
        }

        private static Dictionary<int, int[]> LoadCompany(string path)
        {
            var company = new Dictionary<int, int[]>();
            if (!File.Exists(path)) return company;
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var row in json.RootElement.GetProperty("hunts").EnumerateArray())
                {
                    var monsters = new List<int>();
                    foreach (var monster in row.GetProperty("company").EnumerateArray()) monsters.Add(monster.GetInt32());
                    company[row.GetProperty("hunt").GetInt32()] = monsters.ToArray();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Hunts] Could not read {Path.GetFileName(path)}: {ex.Message}");
            }
            return company;
        }

        private static Dictionary<int, Definition> Load(string path)
        {
            var hunts = new Dictionary<int, Definition>();
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Hunts] {path} not found: no legendary hunt can be launched.");
                return hunts;
            }

            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var entry in json.RootElement.GetProperty("hunts").EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out int id)) continue;
                    var row = entry.Value;
                    hunts[id] = new Definition(id, row.GetProperty("nameId").GetInt32(), row.GetProperty("level").GetInt32(),
                                               row.GetProperty("monster").GetInt32(), row.GetProperty("chest").GetInt32(),
                                               row.GetProperty("mapItem").GetInt32(), row.GetProperty("xpRatio").GetDouble());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Hunts] Could not read {Path.GetFileName(path)}: {ex.Message}");
            }

            Console.WriteLine($"[Hunts] {hunts.Count} legendary hunts.");
            return hunts;
        }
    }
}
