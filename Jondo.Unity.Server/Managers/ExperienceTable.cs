using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jondo.Unity.Launcher
{
    /// <summary>
    /// Character experience table: how much accumulated experience is needed to reach each level.
    /// It comes from the client bundles
    /// (data_assets_characterxpmappingsdataroot.asset.bundle) through extract_character_xp.py.
    ///
    /// The first values line up with what was already hardcoded in the kri -- level 2 = 110 and
    /// level 3 = 650 -- and with the official end-of-fight capture, which for a level 3 character
    /// sends 650 as the floor of the level and 1500 as the threshold for the next one.
    /// </summary>
    public static class ExperienceTable
    {
        private static readonly SortedDictionary<int, long> _floors = new SortedDictionary<int, long>();
        private static int _maxLevel = 1;

        public static bool IsLoaded => _floors.Count > 0;
        public static int MaxLevel => _maxLevel;

        public static void Initialize()
        {
            _floors.Clear();
            string path = Paths.CharacterXpJson;

            if (!File.Exists(path))
            {
                Console.WriteLine($"[ExperienceTable] WARNING: {path} not found. " +
                                  "Experience cannot be computed; run extract_character_xp.py.");
                return;
            }

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (int.TryParse(prop.Name, out int level) && prop.Value.TryGetInt64(out long xp))
                    {
                        _floors[level] = xp;
                    }
                }
                _maxLevel = _floors.Count > 0 ? _floors.Keys.Max() : 1;
                Console.WriteLine($"[ExperienceTable] Experience table loaded: {_floors.Count} levels (up to {_maxLevel}).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ExperienceTable] Error reading {path}: {ex.Message}");
            }
        }

        /// <summary>Accumulated experience the given level starts at.</summary>
        public static long LevelFloor(int level)
        {
            if (_floors.Count == 0) return 0;
            if (_floors.TryGetValue(level, out long xp)) return xp;
            return level <= 1 ? 0 : _floors[Math.Min(level, _maxLevel)];
        }

        /// <summary>Accumulated experience at which the next level is reached.</summary>
        public static long NextLevelFloor(int level)
        {
            if (_floors.Count == 0) return 0;
            int next = level + 1;
            if (next > _maxLevel) return LevelFloor(_maxLevel);
            return LevelFloor(next);
        }

        /// <summary>The level that a given amount of accumulated experience corresponds to.</summary>
        public static int LevelForXp(long experience)
        {
            if (_floors.Count == 0) return 1;
            int level = 1;
            foreach (var kv in _floors)
            {
                if (kv.Value > experience) break;
                level = kv.Key;
            }
            return level;
        }
    }
}
