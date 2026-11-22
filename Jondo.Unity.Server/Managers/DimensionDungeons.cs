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
                                   IReadOnlySet<int> Dungeons, IReadOnlyDictionary<int, IReadOnlyList<int>> Beside)
        {
            public static readonly Rules None = new(0, 0, 0, 100, new HashSet<int>(),
                                                    new Dictionary<int, IReadOnlyList<int>>());

            /// <summary>The dungeons that write their own later waves («waves» on their row).</summary>
            public IReadOnlyDictionary<int, Own> Owns { get; init; } = new Dictionary<int, Own>();

            /// <summary>The life a dungeon's later waves come with: its own, or the rules'.</summary>
            public int LifePercentFor(int dungeon)
                => Owns.TryGetValue(dungeon, out var own) && own.LifePercent is int life ? life : LifePercentFromSecondWave;
        }

        /// <summary>
        /// A dungeon that is not one of the Twelve's but is fought the same way, with waves of its
        /// own: the Memoria de Orukam, in the Dragon's dimension, has three, each written out.
        /// </summary>
        /// <param name="Waves">The waves after the first, in order: the turns before each and who comes.</param>
        /// <param name="LifePercent">The life its later waves come with, when it is not the rules'.</param>
        public sealed record Own(IReadOnlyList<(int Turns, IReadOnlyList<int> Monsters)> Waves, int? LifePercent);

        /// <summary>
        /// The monsters a dimension dungeon's boss starts the fight with («beside»): King Nidas's
        /// Bobolsa. They come with him, once, and never in the later waves.
        /// </summary>
        public static IReadOnlyList<int> BesideTheBoss(int dungeon)
            => Current.Beside.TryGetValue(dungeon, out var beside) ? beside : Array.Empty<int>();

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
        /// never come back, nor what came beside them (<see cref="BesideTheBoss"/>, in
        /// <paramref name="bosses"/> too). Each comes <see cref="Rules.EveryTurns"/> turns after the one before.
        /// </summary>
        /// <remarks>
        /// A dungeon that writes its own waves (<see cref="Own"/>) gets those instead: each as
        /// written, then, for more people than it names, drawn again from its own monsters; each
        /// monster at its grade in the group, or else at the group's.
        /// </remarks>
        public static List<FightInstance.TimedWave> Plan(IReadOnlyList<MobSpawnManager.MobMember> group,
                                                         IReadOnlyCollection<int> bosses, int size, long seed,
                                                         int dungeon = 0)
        {
            var rules = Current;
            var waves = new List<FightInstance.TimedWave>();
            if (rules.Waves < 2 || size <= 0) return waves;
            if (rules.Owns.TryGetValue(dungeon, out var own)) return Written(own, group, bosses, size, seed);

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

        private static List<FightInstance.TimedWave> Written(Own own, IReadOnlyList<MobSpawnManager.MobMember> group,
                                                             IReadOnlyCollection<int> bosses, int size, long seed)
        {
            var grades = group.Where(m => m.Monster != null)
                              .GroupBy(m => m.Monster!.Id).ToDictionary(g => g.Key, g => g.First().GradeIndex);
            int common = group.Where(m => m.Monster != null && !bosses.Contains(m.Monster.Id))
                              .GroupBy(m => m.GradeIndex).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key)
                              .Select(g => g.Key).DefaultIfEmpty(0).First();
            int GradeOf(int monster) => grades.TryGetValue(monster, out int grade) ? grade : common;

            var dice = new Random(unchecked((int)(seed ^ (seed >> 32))));
            var waves = new List<FightInstance.TimedWave>();
            foreach (var (turns, monsters) in own.Waves)
            {
                var members = monsters.Take(size).Select(m => (m, GradeOf(m))).ToList();
                while (members.Count < size)
                {
                    int m = monsters[dice.Next(monsters.Count)];
                    members.Add((m, GradeOf(m)));
                }
                waves.Add(new FightInstance.TimedWave(Math.Max(1, turns), members));
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
                        var beside = new Dictionary<int, IReadOnlyList<int>>();
                        var owns = new Dictionary<int, Own>();
                        if (root.TryGetProperty("dungeons", out var list) && list.ValueKind == JsonValueKind.Array)
                            foreach (var row in list.EnumerateArray())
                            {
                                if (!row.TryGetProperty("dungeon", out var id) || id.ValueKind != JsonValueKind.Number) continue;
                                dungeons.Add(id.GetInt32());
                                if (row.TryGetProperty("beside", out var with) && with.ValueKind == JsonValueKind.Array)
                                {
                                    var monsters = with.EnumerateArray().Where(m => m.TryGetInt32(out int v) && v > 0)
                                                       .Select(m => m.GetInt32()).Distinct().ToList();
                                    if (monsters.Count > 0) beside[id.GetInt32()] = monsters;
                                }
                                if (OwnOf(row) is Own written) owns[id.GetInt32()] = written;
                            }
                        rules = new Rules(Int(root, "waves", 0), Int(root, "everyTurns", 0), Int(root, "arrivalState", 0),
                                          Int(root, "lifePercentFromSecondWave", 100), dungeons, beside)
                                { Owns = owns };
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Content] {AuthoredFile} is unreadable: {ex.Message}");
                }
                _rules = rules;
            }
        }

        /// <summary>A row's own waves: «waves» [{ turns, monsters [id...] }] and «lifePercentFromSecondWave».</summary>
        private static Own? OwnOf(JsonElement row)
        {
            if (!row.TryGetProperty("waves", out var list) || list.ValueKind != JsonValueKind.Array) return null;
            var waves = new List<(int, IReadOnlyList<int>)>();
            foreach (var wave in list.EnumerateArray())
            {
                if (wave.ValueKind != JsonValueKind.Object || !wave.TryGetProperty("monsters", out var who)
                    || who.ValueKind != JsonValueKind.Array) continue;
                var monsters = who.EnumerateArray().Where(m => m.TryGetInt32(out int v) && v > 0)
                                  .Select(m => m.GetInt32()).ToList();
                if (monsters.Count > 0) waves.Add((Int(wave, "turns", 5), monsters));
            }
            if (waves.Count == 0) return null;
            int? life = row.TryGetProperty("lifePercentFromSecondWave", out var l) && l.ValueKind == JsonValueKind.Number
                ? l.GetInt32() : null;
            return new Own(waves, life);
        }

        private static int Int(JsonElement root, string name, int otherwise)
            => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : otherwise;
    }
}
