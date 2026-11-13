using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Monsters of which their maps hold a single one at a time, in one of their groups chosen at
    /// random, and which come back in another once beaten (content/monsters/unique_monsters.json).
    /// </summary>
    /// <remarks>
    /// Written for Lord Crow's library as Ankama remade it on 21 January 2025: its three unique
    /// monsters hide among the sixteen rooms, one of each, and the base's background groups had
    /// them in almost every group, up to four times in one. Wherever a group is read or drawn they
    /// are swapped for the ordinary monster they are a named version of (<see cref="Strip"/>);
    /// <see cref="MobSpawnManager"/> then puts each one that is due in a group of its maps.
    ///
    /// Not the archmonsters' rules: those are drawn one group in ten, one per map, and wherever
    /// their zone is. A unique always stands somewhere unless a fight holds it, and the three may
    /// share a group.
    /// </remarks>
    public static class UniqueMonsters
    {
        public const string File = "monsters/unique_monsters.json";

        public sealed class Unique
        {
            /// <summary>The unique monster.</summary>
            public int Monster { get; init; }

            /// <summary>Who it is swapped for in the groups that have it unasked.</summary>
            public int Ordinary { get; init; }

            /// <summary>The maps where it stands.</summary>
            public IReadOnlyList<long> Maps { get; init; } = Array.Empty<long>();

            /// <summary>How long it stays away once beaten.</summary>
            public int RespawnMinutes { get; init; }
        }

        /// <summary>The time, which the tests move forward instead of waiting.</summary>
        public static Func<DateTime> Clock = () => DateTime.UtcNow;

        private static readonly Dictionary<int, Unique> _uniques = new();

        /// <summary>Where each one stands: monster -> its map and group.</summary>
        private static readonly Dictionary<int, (long Map, long Group)> _placed = new();

        /// <summary>The beaten ones, and when they may stand again.</summary>
        private static readonly Dictionary<int, DateTime> _backAt = new();

        private static volatile bool _loaded;
        private static readonly object _gate = new object();

        /// <summary>Reads the file once, at boot, with its log line.</summary>
        public static void Load() => Ensure();

        /// <summary>The rows read.</summary>
        public static IReadOnlyCollection<Unique> All
        {
            get
            {
                Ensure();
                lock (_gate) return _uniques.Values.ToList();
            }
        }

        /// <summary>Whether a monster is one of them.</summary>
        public static bool Is(int monster)
        {
            Ensure();
            lock (_gate) return _uniques.ContainsKey(monster);
        }

        /// <summary>
        /// Swaps every unique in a group read or drawn for its ordinary version: where it stands is
        /// not the group's to say. How many were swapped.
        /// </summary>
        public static int Strip(List<int> ids)
        {
            Ensure();
            lock (_gate)
            {
                if (_uniques.Count == 0) return 0;
                int swapped = 0;
                for (int i = 0; i < ids.Count; i++)
                {
                    if (!_uniques.TryGetValue(ids[i], out var unique)) continue;
                    ids[i] = unique.Ordinary;
                    swapped++;
                }
                return swapped;
            }
        }

        /// <summary>
        /// The ones that may stand now: standing nowhere -- or where <paramref name="stillThere"/>
        /// says they no longer are, a group taken away without its fight -- and their time away over.
        /// </summary>
        public static List<Unique> Due(Func<int, long, long, bool> stillThere)
        {
            Ensure();
            lock (_gate)
            {
                var now = Clock();
                var due = new List<Unique>();
                foreach (var unique in _uniques.Values)
                {
                    if (_placed.TryGetValue(unique.Monster, out var where))
                    {
                        if (stillThere(unique.Monster, where.Map, where.Group)) continue;
                        _placed.Remove(unique.Monster);
                    }
                    if (_backAt.TryGetValue(unique.Monster, out var at) && at > now) continue;
                    due.Add(unique);
                }
                return due;
            }
        }

        /// <summary>It stands now in that group of that map.</summary>
        public static void Placed(int monster, long map, long group)
        {
            lock (_gate)
            {
                _placed[monster] = (map, group);
                _backAt.Remove(monster);
            }
        }

        /// <summary>Where it stands, if it does.</summary>
        public static (long Map, long Group)? WhereIs(int monster)
        {
            lock (_gate) return _placed.TryGetValue(monster, out var where) ? where : null;
        }

        /// <summary>
        /// A group leaves its map, beaten: the ones it held are away for their respawn time and
        /// then stand again in another group of their maps.
        /// </summary>
        public static void Released(long map, long group)
        {
            lock (_gate)
            {
                foreach (var pair in _placed.Where(p => p.Value.Map == map && p.Value.Group == group).ToList())
                {
                    _placed.Remove(pair.Key);
                    int minutes = _uniques.TryGetValue(pair.Key, out var unique) ? unique.RespawnMinutes : 0;
                    _backAt[pair.Key] = Clock() + TimeSpan.FromMinutes(minutes);
                }
            }
        }

        /// <summary>For the tests: these rows, nobody placed.</summary>
        internal static void Use(IEnumerable<Unique> uniques)
        {
            lock (_gate)
            {
                _uniques.Clear();
                _placed.Clear();
                _backAt.Clear();
                foreach (var unique in uniques) _uniques[unique.Monster] = unique;
                _loaded = true;
            }
        }

        private static void Ensure()
        {
            if (_loaded) return;
            lock (_gate)
            {
                if (_loaded) return;
                try
                {
                    Read();
                }
                finally
                {
                    _loaded = true;   // a missing file counts as tried
                }
            }
        }

        private static void Read()
        {
            string path = Paths.ContentFile(File);
            if (!System.IO.File.Exists(path)) return;

            try
            {
                foreach (var unique in Parse(System.IO.File.ReadAllText(path))) _uniques[unique.Monster] = unique;
                Console.WriteLine($"[Uniques] {_uniques.Count} unique monster(s), one of each on their maps.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Uniques] Could not read {File}: {ex.Message}");
            }
        }

        /// <summary>The rows of the file's text; those without a monster, an ordinary one or a map are left out.</summary>
        public static List<Unique> Parse(string json)
        {
            var uniques = new List<Unique>();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("uniques", out var list) || list.ValueKind != JsonValueKind.Array) return uniques;

            foreach (var row in list.EnumerateArray())
            {
                var maps = new List<long>();
                if (row.TryGetProperty("maps", out var array) && array.ValueKind == JsonValueKind.Array)
                    foreach (var map in array.EnumerateArray())
                        if (map.TryGetInt64(out long id) && id > 0 && !maps.Contains(id)) maps.Add(id);

                var unique = new Unique
                {
                    Monster = (int)Number(row, "monster"),
                    Ordinary = (int)Number(row, "ordinary"),
                    Maps = maps,
                    RespawnMinutes = (int)Math.Max(0, Number(row, "respawnMinutes")),
                };
                if (unique.Monster > 0 && unique.Ordinary > 0 && unique.Ordinary != unique.Monster && maps.Count > 0)
                    uniques.Add(unique);
            }
            return uniques;
        }

        private static long Number(JsonElement row, string name)
            => row.TryGetProperty(name, out var value) && value.TryGetInt64(out long number) ? number : 0;
    }
}
