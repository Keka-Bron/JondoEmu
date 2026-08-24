using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// Loads the small set of effect classifications not represented by numeric EffectData.
    /// Ordinary effect signs come directly from characteristicOperator; this file is only for
    /// characteristic-zero AP/MP steals and multiplicative effect families.
    /// </summary>
    public static class EffectRuntimeSemantics
    {
        private static IReadOnlyDictionary<int, int> _pointSteals = new Dictionary<int, int>();
        private static IReadOnlySet<int> _multipliers = new HashSet<int>();

        public static void Initialize()
        {
            string path = Paths.EffectRuntimeSemanticsJson;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("clientVersion", out JsonElement version) ||
                version.GetString() != Paths.ActiveClientDataVersion)
                throw new InvalidDataException("effect_runtime_semantics.json has the wrong client version.");

            var steals = new Dictionary<int, int>();
            if (!root.TryGetProperty("pointSteals", out JsonElement entries) || entries.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("effect_runtime_semantics.json has no pointSteals array.");
            foreach (JsonElement entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("effectId", out JsonElement effect) || !effect.TryGetInt32(out int effectId) || effectId <= 0 ||
                    !entry.TryGetProperty("characteristicId", out JsonElement characteristic) || !characteristic.TryGetInt32(out int characteristicId) || characteristicId <= 0 ||
                    !steals.TryAdd(effectId, characteristicId))
                    throw new InvalidDataException("Invalid or duplicate point-steal effect mapping.");
            }

            var multipliers = new HashSet<int>();
            if (!root.TryGetProperty("multipliers", out JsonElement multiplierEntries) || multiplierEntries.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("effect_runtime_semantics.json has no multipliers array.");
            foreach (JsonElement entry in multiplierEntries.EnumerateArray())
                if (!entry.TryGetInt32(out int effectId) || effectId <= 0 || !multipliers.Add(effectId))
                    throw new InvalidDataException("Invalid or duplicate multiplier effect mapping.");

            _pointSteals = steals;
            _multipliers = multipliers;
            Console.WriteLine($"[EffectSemantics] {steals.Count} point steals, {multipliers.Count} multipliers.");
        }

        public static int PointStealCharacteristic(int effectId)
            => _pointSteals.TryGetValue(effectId, out int characteristic) ? characteristic : 0;
        public static bool IsMultiplier(int effectId) => _multipliers.Contains(effectId);
    }
}
