using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// Loads version-pinned, external dungeon/monster mechanic specifications.  A specification
    /// is intentionally inert until a measured fight handler explicitly consumes its rule kind;
    /// this prevents a prose guide from becoming guessed combat code.
    /// </summary>
    public static class MechanicCatalog
    {
        public sealed class Definition
        {
            public string Id { get; init; } = "";
            public string Kind { get; init; } = "";
            public string Status { get; init; } = "Draft";
            public string SourceUrl { get; init; } = "";
            public JsonElement Document { get; init; }
        }

        /// <summary>
        /// An external, data-only policy consumed by the generic monster AI.  It deliberately
        /// cannot run arbitrary code or emit packets: new encounter behaviour still needs a
        /// measured generic rule kind in the fight engine.
        /// </summary>
        public sealed class MonsterAiPolicy
        {
            public int MonsterId { get; init; }
            public double? FleeBelowHpPercent { get; init; }
            public IReadOnlyDictionary<int, int> SpellPriorities { get; init; } =
                new Dictionary<int, int>();
        }

        private static IReadOnlyDictionary<string, Definition> _all =
            new Dictionary<string, Definition>(StringComparer.Ordinal);
        private static IReadOnlyDictionary<int, MonsterAiPolicy> _monsterAi =
            new Dictionary<int, MonsterAiPolicy>();

        public static IReadOnlyDictionary<string, Definition> All => _all;
        public static bool TryGet(string id, out Definition definition) => _all.TryGetValue(id, out definition!);
        public static bool TryGetMonsterAiPolicy(int monsterId, out MonsterAiPolicy policy)
            => _monsterAi.TryGetValue(monsterId, out policy!);

        public static void Initialize() => Reload();

        /// <summary>Reloads only complete, manifest-listed files from client_data. Safe to call on an admin-triggered content reload.</summary>
        public static void Reload()
        {
            var loaded = new Dictionary<string, Definition>(StringComparer.Ordinal);
            var monsterAi = new Dictionary<int, MonsterAiPolicy>();
            string manifestPath = Path.Combine(Paths.MechanicsDir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                _all = loaded;
                _monsterAi = monsterAi;
                Console.WriteLine($"[Mechanics] No external mechanic manifest at {manifestPath}; no guide mechanics loaded.");
                return;
            }

            try
            {
                using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (!manifest.RootElement.TryGetProperty("clientVersion", out var version) ||
                    version.GetString() != Paths.PinnedClientVersion)
                    throw new InvalidOperationException("mechanic manifest clientVersion does not match the running server.");
                if (!manifest.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("mechanic manifest has no entries array.");

                foreach (var entry in entries.EnumerateArray())
                {
                    string relative = RequiredText(entry, "file");
                    if (Path.IsPathRooted(relative) || relative.Contains("..", StringComparison.Ordinal))
                        throw new InvalidOperationException($"unsafe mechanic path: {relative}");
                    string file = Path.GetFullPath(Path.Combine(Paths.MechanicsDir, relative));
                    string root = Path.GetFullPath(Paths.MechanicsDir) + Path.DirectorySeparatorChar;
                    if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
                        throw new InvalidOperationException($"missing mechanic file: {relative}");

                    using var document = JsonDocument.Parse(File.ReadAllText(file));
                    string id = RequiredText(document.RootElement, "id");
                    string kind = RequiredText(document.RootElement, "kind");
                    string status = RequiredText(document.RootElement, "status");
                    string sourceUrl = RequiredText(document.RootElement, "sourceUrl");
                    if (!document.RootElement.TryGetProperty("clientVersion", out JsonElement documentVersion) ||
                        documentVersion.GetString() != Paths.PinnedClientVersion)
                        throw new InvalidOperationException($"{id}: clientVersion does not match the running server.");
                    if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                        throw new InvalidOperationException($"{id}: sourceUrl must be HTTPS.");
                    if (!string.Equals(status, "Verified", StringComparison.Ordinal) &&
                        !string.Equals(status, "Draft", StringComparison.Ordinal))
                        throw new InvalidOperationException($"{id}: status must be Draft or Verified.");
                    if (!loaded.TryAdd(id, new Definition { Id = id, Kind = kind, Status = status, SourceUrl = sourceUrl, Document = document.RootElement.Clone() }))
                        throw new InvalidOperationException($"duplicate mechanic id: {id}");

                    // Only verified, explicitly declared policies may affect generic AI.  A guide
                    // summary in a Draft mechanic remains documentation, never live combat logic.
                    if (kind == "monster-mechanic-map" && status == "Verified")
                    {
                        MonsterAiPolicy policy = ParseMonsterAiPolicy(id, document.RootElement);
                        if (!monsterAi.TryAdd(policy.MonsterId, policy))
                            throw new InvalidOperationException($"duplicate monster AI policy for monster {policy.MonsterId}");
                    }
                }
                _all = loaded;
                _monsterAi = monsterAi;
                int verified = 0;
                foreach (var definition in loaded.Values) if (definition.Status == "Verified") verified++;
                Console.WriteLine($"[Mechanics] {loaded.Count} external definition(s) loaded ({verified} verified, {loaded.Count - verified} draft).");
            }
            catch (Exception ex)
            {
                _all = new Dictionary<string, Definition>(StringComparer.Ordinal);
                _monsterAi = new Dictionary<int, MonsterAiPolicy>();
                Console.WriteLine($"[Mechanics] Rejected external mechanic map: {ex.Message}");
            }
        }

        private static string RequiredText(JsonElement value, string name)
        {
            if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(property.GetString()))
                throw new InvalidOperationException($"missing required string '{name}'.");
            return property.GetString()!;
        }

        private static MonsterAiPolicy ParseMonsterAiPolicy(string id, JsonElement document)
        {
            if (!document.TryGetProperty("monster", out JsonElement monster) || monster.ValueKind != JsonValueKind.Object ||
                !monster.TryGetProperty("clientMonsterId", out JsonElement monsterId) || !monsterId.TryGetInt32(out int parsedId) || parsedId <= 0)
                throw new InvalidOperationException($"{id}: verified monster mechanic requires monster.clientMonsterId.");

            if (!document.TryGetProperty("ai", out JsonElement ai) || ai.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"{id}: verified monster mechanic requires an ai object.");

            double? flee = null;
            if (ai.TryGetProperty("fleeBelowHpPercent", out JsonElement fleeValue))
            {
                if (!fleeValue.TryGetDouble(out double parsedFlee) || parsedFlee < 0 || parsedFlee > 1)
                    throw new InvalidOperationException($"{id}: ai.fleeBelowHpPercent must be between 0 and 1.");
                flee = parsedFlee;
            }

            var priorities = new Dictionary<int, int>();
            if (ai.TryGetProperty("spellPriorities", out JsonElement entries))
            {
                if (entries.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException($"{id}: ai.spellPriorities must be an array.");
                foreach (JsonElement entry in entries.EnumerateArray())
                {
                    if (!entry.TryGetProperty("spellId", out JsonElement spellId) || !spellId.TryGetInt32(out int parsedSpell) || parsedSpell <= 0 ||
                        !entry.TryGetProperty("priority", out JsonElement priority) || !priority.TryGetInt32(out int parsedPriority))
                        throw new InvalidOperationException($"{id}: each spell priority requires positive spellId and integer priority.");
                    if (!priorities.TryAdd(parsedSpell, parsedPriority))
                        throw new InvalidOperationException($"{id}: spell {parsedSpell} is listed twice.");
                }
            }

            if (flee is null && priorities.Count == 0)
                throw new InvalidOperationException($"{id}: verified monster AI has no executable policy.");
            return new MonsterAiPolicy { MonsterId = parsedId, FleeBelowHpPercent = flee, SpellPriorities = priorities };
        }
    }
}
