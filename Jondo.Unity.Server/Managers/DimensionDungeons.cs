using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The dungeons of the divine dimensions (content/fights/dimension_dungeons.json): one room,
    /// whose boss is fought in waves that come by the clock.
    /// </summary>
    /// <remarks>
    /// Dofus pour les noobs, on every one of them: five waves, the next one every five turns or as
    /// soon as nobody is left in front; each as many monsters as there are fighters, the first led
    /// by the boss; every newcomer invulnerable until the end of its first turn and, from the second
    /// wave on, with a quarter less life. Until this, the room's group of eight came in at once.
    /// </remarks>
    public static class DimensionDungeons
    {
        /// <summary>The file, relative to the content root.</summary>
        public const string AuthoredFile = "fights/dimension_dungeons.json";

        /// <summary>What every one of these dungeons follows.</summary>
        public sealed record Rules(int Waves, int EveryTurns, int ArrivalState, int LifePercentFromSecondWave,
                                   IReadOnlySet<int> Dungeons)
        {
            public static readonly Rules None = new(0, 0, 0, 100, new HashSet<int>());
        }

        private static Rules? _rules;
        private static readonly object _lock = new();

        /// <summary>The rules as written, read once.</summary>
        public static Rules Current
        {
            get
            {
                Load();
                return _rules!;
            }
        }

        /// <summary>
        /// Whether a fight against this map's group is a dimension dungeon's: the map is the boss
        /// room of one of the listed dungeons.
        /// </summary>
        public static bool FightsInWaves(long roleplayMapId)
        {
            var rules = Current;
            if (rules.Waves < 2 || !DungeonManager.IsLoaded) return false;
            var dungeon = DungeonManager.OfRoom(roleplayMapId);
            return dungeon != null && rules.Dungeons.Contains(dungeon.Id) && dungeon.LastRoom == roleplayMapId;
        }

        /// <summary>
        /// The waves after the first, for a fight against <paramref name="group"/>: one fewer than
        /// the rules' count, each of <paramref name="size"/> monsters drawn from the group's own --
        /// every kind once in a shuffled order, then drawn again -- at their grades; the bosses
        /// never come back. Each comes <see cref="Rules.EveryTurns"/> turns after the one before.
        /// </summary>
        public static List<FightInstance.TimedWave> Plan(IReadOnlyList<MobSpawnManager.MobMember> group,
                                                         IReadOnlyCollection<int> bosses, int size, long seed)
        {
            var rules = Current;
            var waves = new List<FightInstance.TimedWave>();
            if (rules.Waves < 2 || size <= 0) return waves;

            var kinds = group.Where(m => m.Monster != null && !bosses.Contains(m.Monster.Id))
                             .GroupBy(m => m.Monster!.Id)
                             .Select(g => (Monster: g.Key, Grade: g.First().GradeIndex))
                             .OrderBy(k => k.Monster)
                             .ToList();
            if (kinds.Count == 0) return waves;

            var dice = new Random(unchecked((int)(seed ^ (seed >> 32))));
            for (int wave = 1; wave < rules.Waves; wave++)
            {
                var members = new List<(int Monster, int Grade)>();
                foreach (var kind in kinds.OrderBy(_ => dice.Next()))
                {
                    if (members.Count >= size) break;
                    members.Add(kind);
                }
                while (members.Count < size) members.Add(kinds[dice.Next(kinds.Count)]);
                waves.Add(new FightInstance.TimedWave(Math.Max(1, rules.EveryTurns), members));
            }
            return waves;
        }

        /// <summary>For the content reload and the tests: read the file again.</summary>
        internal static void Forget()
        {
            lock (_lock) _rules = null;
        }

        private static void Load()
        {
            lock (_lock)
            {
                if (_rules != null) return;
                var rules = Rules.None;
                try
                {
                    string path = Paths.ContentFile(AuthoredFile);
                    if (File.Exists(path))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(path));
                        var root = doc.RootElement;
                        var dungeons = new HashSet<int>();
                        if (root.TryGetProperty("dungeons", out var list) && list.ValueKind == JsonValueKind.Array)
                            foreach (var row in list.EnumerateArray())
                                if (row.TryGetProperty("dungeon", out var id) && id.ValueKind == JsonValueKind.Number)
                                    dungeons.Add(id.GetInt32());
                        rules = new Rules(Int(root, "waves", 0), Int(root, "everyTurns", 0), Int(root, "arrivalState", 0),
                                          Int(root, "lifePercentFromSecondWave", 100), dungeons);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Content] {AuthoredFile} is unreadable: {ex.Message}");
                }
                _rules = rules;
            }
        }

        private static int Int(JsonElement root, string name, int otherwise)
            => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : otherwise;
    }
}
