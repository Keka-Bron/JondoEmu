using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The Infinite Dreams' tables: what the client knows of them and the loot table the captures
    /// measured. Read once, from datos/suenos_3.6.10.10.json.
    /// </summary>
    /// <remarks>
    /// ─── Where it comes from ─────────────────────────────────────────────────────────────────
    ///
    ///   intensities   the client's InfiniteDreamIntensitiesDataRoot, one row per difficulty 1..10:
    ///                 dropBonus, xpBonus, money, additionalLife, droplegend, dreamFragments
    ///   rewards       the client's InfiniteDreamRewardsDataRoot: a reward's name and its actions
    ///   actions       the client's InfiniteDreamRewardActionsDataRoot: an action's effect
    ///   loot          the izo of the five captures that open the loot table, each line's percent
    ///                 taken back to a loot bonus of 100
    ///
    /// The file's own comment says how each column is read. The generator is extraer_suenos.py,
    /// which reads the bundles with UnityPy and the captures with tools/hilo.py.
    ///
    /// Three of the intensity columns were already measured, and the client agrees with the
    /// captures on all three: money is <see cref="Dreams.StartingDreamPoints"/>, additionalLife is
    /// <see cref="Dreams.StartingArenas"/> and dropBonus is <see cref="Dreams.BonusOf"/> over 100.
    /// </remarks>
    public static class DreamData
    {
        public const string DataFile = "suenos_3.6.10.10.json";

        /// <summary>One intensity of the client's InfiniteDreamIntensitiesDataRoot.</summary>
        public sealed record Intensity(int Level, double DropBonus, double XpBonus, int Money,
                                       int AdditionalLife, bool DropsLegends, int DreamFragments);

        /// <summary>
        /// A line of the dream's loot table: an item, the criterion it drops under, and its
        /// percent at a loot bonus of 100. <see cref="Scaled"/> false for the lines that are 100
        /// at every captured bonus; <see cref="Reflections"/> for the one line whose quantity,
        /// and not its chance, grows with the bonus.
        /// </summary>
        public sealed record LootLine(int Item, string Criterion, double Percent, bool Scaled, bool Reflections);

        /// <summary>A row of InfiniteDreamRewardsDataRoot: its name and what it does.</summary>
        public sealed record RewardRow(int Id, int NameId, IReadOnlyList<int> Actions);

        private static readonly object _lock = new object();
        private static bool _loaded;
        private static readonly Dictionary<int, Intensity> _intensities = new();
        private static readonly Dictionary<int, RewardRow> _rewards = new();
        private static readonly Dictionary<int, int> _actions = new();
        private static readonly List<LootLine> _loot = new();

        /// <summary>The intensity of a difficulty, or null when the file does not have it.</summary>
        public static Intensity? IntensityOf(int difficulty)
        {
            Load();
            return _intensities.TryGetValue(difficulty, out var intensity) ? intensity : null;
        }

        /// <summary>The loot table, in the order of the izo it was read from.</summary>
        public static IReadOnlyList<LootLine> Loot
        {
            get { Load(); return _loot; }
        }

        /// <summary>A reward row, or null.</summary>
        public static RewardRow? RewardOf(int id)
        {
            Load();
            return _rewards.TryGetValue(id, out var row) ? row : null;
        }

        /// <summary>The effect an action applies, or zero for the four the table does not hold.</summary>
        public static int EffectOfAction(int action)
        {
            Load();
            return _actions.TryGetValue(action, out int effect) ? effect : 0;
        }

        /// <summary>
        /// The actions no row of InfiniteDreamRewardActionsDataRoot holds, read off the rewards
        /// that carry them: reward 4 "Puntos de sueño" is fifteen 14s and travels with an f5 of
        /// 15; 13 "Arena de Draconiros" is one 17; 24 "Tormenta astral" one 38, and 118 -- one 38
        /// and five 14s -- adds a storm and five dream points in the captures; 154 "50 niveles
        /// de soñador" is one 156 and travels with an f4 of 50.
        /// </summary>
        public const int DreamPointAction = 14;
        public const int SandAction = 17;
        public const int StormAction = 38;
        public const int DreamerLevelAction = 156;

        private static void Load()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                try
                {
                    string path = Paths.Resolve(DataFile);
                    if (!File.Exists(path))
                    {
                        Console.WriteLine($"[Sueños] {DataFile} is missing: no dream loot, no dream fragments.");
                        return;
                    }

                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    var root = doc.RootElement;

                    foreach (var row in root.GetProperty("intensities").EnumerateArray())
                    {
                        var intensity = new Intensity(
                            row.GetProperty("intensity").GetInt32(),
                            row.GetProperty("dropBonus").GetDouble(),
                            row.GetProperty("xpBonus").GetDouble(),
                            row.GetProperty("money").GetInt32(),
                            row.GetProperty("additionalLife").GetInt32(),
                            row.GetProperty("droplegend").GetInt32() != 0,
                            row.GetProperty("dreamFragments").GetInt32());
                        _intensities[intensity.Level] = intensity;
                    }

                    foreach (var entry in root.GetProperty("rewards").EnumerateObject())
                    {
                        if (!int.TryParse(entry.Name, out int id)) continue;
                        var actions = entry.Value.GetProperty("actions").EnumerateArray().Select(a => a.GetInt32()).ToList();
                        _rewards[id] = new RewardRow(id, entry.Value.GetProperty("nameId").GetInt32(), actions);
                    }

                    foreach (var entry in root.GetProperty("actions").EnumerateObject())
                        if (int.TryParse(entry.Name, out int id)) _actions[id] = entry.Value.GetInt32();

                    foreach (var line in root.GetProperty("loot").EnumerateArray())
                    {
                        bool reflections = line.TryGetProperty("reflections", out var r) && r.ValueKind == JsonValueKind.True;
                        _loot.Add(new LootLine(
                            line.GetProperty("item").GetInt32(),
                            line.TryGetProperty("criterion", out var c) ? c.GetString() ?? "" : "",
                            line.TryGetProperty("percent", out var p) ? p.GetDouble() : 100,
                            !line.TryGetProperty("scaled", out var s) || s.ValueKind != JsonValueKind.False,
                            reflections));
                    }

                    Console.WriteLine($"[Sueños] {_intensities.Count} intensities, {_rewards.Count} rewards and " +
                                      $"{_loot.Count} loot lines from {DataFile}.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Sueños] {DataFile} could not be read: {ex.Message}");
                }
                finally
                {
                    _loaded = true;
                }
            }
        }
    }
}
