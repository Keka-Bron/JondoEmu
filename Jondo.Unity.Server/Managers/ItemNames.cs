using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// Localized item labels from the version-pinned DofusDude snapshot. This is display data,
    /// not player state: refreshing a client_data version refreshes these labels on next start.
    /// </summary>
    public static class ItemNames
    {
        private static readonly Dictionary<string, Dictionary<int, string>> ByLanguage =
            new(StringComparer.OrdinalIgnoreCase);

        public static void Initialize()
        {
            ByLanguage.Clear();
            foreach (string language in new[] { "fr", "en", "es" })
                Load(language);
        }

        public static string Of(int gid, string? language = null)
        {
            string requested = string.IsNullOrWhiteSpace(language) ? "fr" : language!;
            if (ByLanguage.TryGetValue(requested, out var names) &&
                names.TryGetValue(gid, out string? name) && !string.IsNullOrWhiteSpace(name))
                return name;

            // The French catalogue is shipped alongside the server snapshot and is the safe
            // fallback when a language extraction has not been imported yet.
            if (ByLanguage.TryGetValue("fr", out var french) && french.TryGetValue(gid, out name))
                return name;
            return $"item #{gid}";
        }

        private static void Load(string language)
        {
            string path = Path.Combine(Paths.DofusDudeSnapshotDir, language, "equipment.json");
            if (!File.Exists(path)) return;

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.ValueKind != JsonValueKind.Array) return;

                var names = new Dictionary<int, string>();
                foreach (var row in document.RootElement.EnumerateArray())
                {
                    if (!row.TryGetProperty("ankama_id", out var id) || !id.TryGetInt32(out int gid) ||
                        !row.TryGetProperty("name", out var label) || label.ValueKind != JsonValueKind.String)
                        continue;
                    string? name = label.GetString();
                    if (!string.IsNullOrWhiteSpace(name)) names[gid] = name;
                }
                if (names.Count > 0) ByLanguage[language] = names;
                Console.WriteLine($"[ItemNames] {names.Count} localized item names ({language}).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ItemNames] Could not read {Path.GetFileName(path)}: {ex.Message}");
            }
        }
    }
}
