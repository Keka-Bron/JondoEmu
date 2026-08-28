using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Which monsters are archmonsters, where each one belongs, and how thin they have to be
    /// spread.
    ///
    /// A monster is an archmonster when it is somebody else's `correspondingMiniBossId`: the
    /// client's data pairs every ordinary monster with the rare version of itself. There are 306
    /// of them and all 306 declare the subareas they belong to.
    ///
    /// What the world looked like before this existed:
    ///
    ///   39,9 % of the groups had at least one       35.518 placed
    ///   up to 8 in a single group                   5.802 maps with more than one
    ///
    /// The rules, all four of them:
    ///
    ///   - one per group at most
    ///   - one per map at most
    ///   - one in ten groups, not four in ten
    ///   - and one of each per zone: if the Dark Treechnid is standing on a map of its subarea, it
    ///     is on no other map of that subarea until something kills it
    ///
    /// The draw is deterministic — seeded from the group's own id — so a map looks the same after
    /// a restart as it did before. Nothing is rewritten in the database: the 38.744 groups it ships
    /// with are left alone and thinned as they are read.
    ///
    /// An archmonster that loses its place is not deleted but demoted: it is swapped for the
    /// ordinary monster it is the rare version of, so the group keeps its size and its level.
    /// </summary>
    public static class Archimonsters
    {
        /// <summary>One group in ten gets to keep its archmonster.</summary>
        private const int OneInHowMany = 10;

        /// <summary>archmonster id -> the ordinary monster it is the rare version of.</summary>
        private static readonly Dictionary<int, int> _ordinary = new Dictionary<int, int>();

        /// <summary>archmonster id -> the subareas it belongs to.</summary>
        private static readonly Dictionary<int, List<int>> _zones = new Dictionary<int, List<int>>();

        /// <summary>Where each archmonster is standing right now: id -> map.</summary>
        private static readonly Dictionary<int, long> _placed = new Dictionary<int, long>();

        /// <summary>Maps that already have one, so no map gets a second.</summary>
        private static readonly HashSet<long> _busyMaps = new HashSet<long>();

        public static int Count => _ordinary.Count;
        public static bool Is(int monsterId) => _ordinary.ContainsKey(monsterId);

        public static void Initialize(SqliteConnection connection)
        {
            _ordinary.Clear();
            _zones.Clear();
            _placed.Clear();
            _busyMaps.Clear();

            var raw = new Dictionary<int, string>();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, Data FROM MonsterTemplates;";
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (reader.IsDBNull(1)) continue;
                    raw[reader.GetInt32(0)] = reader.GetString(1);
                }
            }

            // Pass one: who is the rare version of whom.
            foreach (var pair in raw)
            {
                try
                {
                    using var doc = JsonDocument.Parse(pair.Value);
                    if (!doc.RootElement.TryGetProperty("correspondingMiniBossId", out var mini)) continue;
                    if (!mini.TryGetInt32(out int rare) || rare == 0) continue;
                    _ordinary[rare] = pair.Key;
                }
                catch { }
            }

            // Pass two: where each of them belongs.
            foreach (int rare in _ordinary.Keys)
            {
                if (!raw.TryGetValue(rare, out string? json)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("subareas", out var subareas)) continue;
                    if (!subareas.TryGetProperty("Array", out var array)) continue;

                    var zones = new List<int>();
                    foreach (var z in array.EnumerateArray())
                    {
                        if (z.TryGetInt32(out int id)) zones.Add(id);
                    }
                    if (zones.Count > 0) _zones[rare] = zones;
                }
                catch { }
            }

            Console.WriteLine($"[Archimonsters] {_ordinary.Count} archmonsters, " +
                              $"{_zones.Count} of them with a zone of their own.");
        }

        /// <summary>
        /// Thins the archmonsters out of one group, and says which one it kept, if any.
        ///
        /// <paramref name="ids"/> is the group as the database has it and comes back trimmed:
        /// every archmonster that does not get to stay is swapped for its ordinary version.
        /// </summary>
        public static int Thin(long mapId, long groupId, List<int> ids)
        {
            int kept = 0;

            for (int i = 0; i < ids.Count; i++)
            {
                int rare = ids[i];
                if (!_ordinary.TryGetValue(rare, out int ordinary)) continue;

                if (kept == 0 && MayStand(mapId, groupId, rare))
                {
                    kept = rare;
                    Reserve(mapId, rare);
                    continue;
                }

                ids[i] = ordinary;
            }

            return kept;
        }

        /// <summary>
        /// The four rules at once. The draw is worked out from the group's id, so it always comes
        /// out the same for the same group, restart or no restart.
        /// </summary>
        private static bool MayStand(long mapId, long groupId, int rare)
        {
            if (_busyMaps.Contains(mapId)) return false;          // one per map
            if (_placed.ContainsKey(rare)) return false;          // one of each, anywhere

            // One in ten. Mixing the group and the monster keeps two groups on the same map from
            // drawing alike.
            return Draw(groupId, rare) % OneInHowMany == 0;
        }

        private static void Reserve(long mapId, int rare)
        {
            _placed[rare] = mapId;
            _busyMaps.Add(mapId);
        }

        /// <summary>
        /// Lets go of the archmonster a map was holding, so the zone can have it again somewhere
        /// else. For when the group is beaten.
        /// </summary>
        public static void Release(long mapId)
        {
            var loose = new List<int>();
            foreach (var pair in _placed)
            {
                if (pair.Value == mapId) loose.Add(pair.Key);
            }
            foreach (int rare in loose) _placed.Remove(rare);
            _busyMaps.Remove(mapId);
        }

        /// <summary>Is this map free to hold one?</summary>
        public static bool MapIsFree(long mapId) => !_busyMaps.Contains(mapId);

        /// <summary>
        /// One that belongs in this subarea and is not standing anywhere at the moment, or zero.
        /// This is what lets a killed archmonster turn up again somewhere else in its own zone.
        /// </summary>
        public static int FreeInZone(int subAreaId)
        {
            foreach (var pair in _zones)
            {
                if (_placed.ContainsKey(pair.Key)) continue;
                if (pair.Value.Contains(subAreaId)) return pair.Key;
            }
            return 0;
        }

        public static int OrdinaryVersionOf(int rare)
            => _ordinary.TryGetValue(rare, out int ordinary) ? ordinary : rare;

        /// <summary>
        /// A number that depends only on the group and the monster. Deliberately not Random: the
        /// same world has to come back after a restart, and nothing here is worth persisting.
        /// </summary>
        private static uint Draw(long groupId, int rare)
        {
            unchecked
            {
                ulong x = ((ulong)groupId * 0x9E3779B97F4A7C15UL) ^ ((ulong)rare * 0xBF58476D1CE4E5B9UL);
                x ^= x >> 33;
                x *= 0xFF51AFD7ED558CCDUL;
                x ^= x >> 33;
                return (uint)(x & 0x7FFFFFFF);
            }
        }
    }
}
