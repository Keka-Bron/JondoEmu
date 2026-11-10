using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.World.Quests
{
    /// <summary>
    /// Start conditions the catalogue leaves out and the game has (content/quests/criteria.json).
    /// </summary>
    /// <remarks>
    /// The catalogue gives «Un pueblo sobre las nubes» «BT=1», always startable, yet the quest is
    /// handed over at the village gate, past the end of the celestial temple: Dofus pour les noobs
    /// lists «Réponses à tout» as the quest that has to be done first. A criterion written here
    /// replaces the catalogue's.
    /// </remarks>
    public static class QuestCriteria
    {
        /// <summary>The file, relative to the content root.</summary>
        public const string AuthoredFile = "quests/criteria.json";

        private static Dictionary<int, string>? _criteria;
        private static Dictionary<int, string>? _objectives;
        private static readonly object _lock = new();

        /// <summary>The criterion of a quest: the authored one when there is one, else the catalogue's.</summary>
        public static string Of(int questId, string catalogue)
        {
            Load();
            return _criteria!.TryGetValue(questId, out string? written) ? written : catalogue;
        }

        /// <summary>
        /// The condition an objective holds for, or empty: «Consejo de clase» lists the nineteen
        /// class masters and each character has only his own to see (PG, the class).
        /// </summary>
        public static string OfObjective(int objectiveId)
        {
            Load();
            return _objectives!.TryGetValue(objectiveId, out string? written) ? written : "";
        }

        /// <summary>For the tests: read the file again.</summary>
        public static void Forget()
        {
            lock (_lock) { _criteria = null; _objectives = null; }
        }

        private static void Load()
        {
            lock (_lock)
            {
                if (_criteria != null) return;
                var criteria = new Dictionary<int, string>();
                var objectives = new Dictionary<int, string>();
                try
                {
                    string path = Paths.ContentFile(AuthoredFile);
                    if (File.Exists(path))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(path));
                        foreach (var row in doc.RootElement.GetProperty("criteria").EnumerateArray())
                        {
                            int quest = row.GetProperty("quest").GetInt32();
                            string criterion = row.GetProperty("criterion").GetString() ?? "";
                            if (quest > 0) criteria[quest] = criterion;
                        }
                        if (doc.RootElement.TryGetProperty("objectives", out var rows))
                        {
                            foreach (var row in rows.EnumerateArray())
                            {
                                int objective = row.GetProperty("objective").GetInt32();
                                string criterion = row.GetProperty("criterion").GetString() ?? "";
                                if (objective > 0) objectives[objective] = criterion;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Content] {AuthoredFile} is unreadable: {ex.Message}");
                }
                _criteria = criteria;
                _objectives = objectives;
            }
        }
    }
}
