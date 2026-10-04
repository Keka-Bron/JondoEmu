using System;
using System.Collections.Generic;
using System.Linq;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The Koliseo's queue: who waits, in which mode, since when, and who can be matched with
    /// whom.
    /// </summary>
    /// <remarks>
    /// One queue per mode: whoever enrols for 3v3 cannot fill a 1v1. What waits is a UNIT -- one
    /// player, or a party enrolled together (lsm), which is never split and always fights on one
    /// side. Static for the reason <see cref="Duels"/> is: the queue spans as many connections as
    /// it has players.
    ///
    /// ─── The matchmaking ──────────────────────────────────────────────────────────────────
    ///
    /// The official site: matchmaking goes by rating, and trades a little of the fight's quality
    /// for a shorter wait (devblog "Devblog : les ligues", 2023). And what this server asks of it
    /// on top: nobody faces, or fights beside, someone of a much lower or higher level, unless
    /// the ladder says they are even. So two players can meet when
    ///
    ///   * their ratings are within the window, <see cref="RatingWindowBase"/> points widening by
    ///     <see cref="RatingWindowPerSecond"/> a second of the oldest one's wait, up to
    ///     <see cref="RatingWindowMax"/>; and
    ///   * their levels are within <see cref="LevelGap"/> -- a gap that does NOT widen with the
    ///     wait -- or both are placed and their ratings within <see cref="LadderEven"/>.
    ///
    /// The members of one party are exempt among themselves: they chose each other. The oldest
    /// unit is served first; the others are tried by closeness of rating; the two sides are
    /// then split so their average ratings are as even as the units allow, the first to arrive
    /// on the blue side when it makes no difference. All the numbers are INFERRED.
    /// </remarks>
    public static class KoliseoQueue
    {
        /// <summary>Levels apart two players may be, whatever the wait.</summary>
        public const int LevelGap = 20;

        /// <summary>Rating apart two placed players may be to meet whatever their levels.</summary>
        public const int LadderEven = 100;

        public const int RatingWindowBase = 150;
        public const int RatingWindowPerSecond = 10;
        public const int RatingWindowMax = 2000;

        /// <summary>What the matchmaking needs to know of a player.</summary>
        public readonly record struct Profile(int Level, int Rating, bool Placed);

        /// <summary>One player waiting.</summary>
        public sealed class Waiting
        {
            public long CharacterId { get; init; }
            public int Mode { get; init; }
            public Profile Profile { get; init; }
        }

        /// <summary>One or more players who wait, and will fight, together.</summary>
        private sealed class Unit
        {
            public long Sequence { get; init; }
            public DateTime Since { get; init; }
            public List<Waiting> Members { get; } = new();
            public double Rating => Members.Average(m => m.Profile.Rating);
        }

        /// <summary>
        /// How a character is seen when he enrols: his level and his standing in the mode. Tests
        /// replace it; unknown characters come out at level 0, which no level gap excludes.
        /// </summary>
        internal static Func<long, int, Profile> Describe = DescribeFromTheWorld;

        /// <summary>For tests: what "now" is.</summary>
        internal static Func<DateTime> Clock = () => DateTime.UtcNow;

        private static readonly object _gate = new object();
        private static readonly Dictionary<int, List<Unit>> _byMode = new();
        private static long _sequence;

        public static int Count
        {
            get
            {
                lock (_gate) return _byMode.Values.Sum(units => units.Sum(u => u.Members.Count));
            }
        }

        public static int CountIn(int mode)
        {
            lock (_gate) return _byMode.TryGetValue(mode, out var units) ? units.Sum(u => u.Members.Count) : 0;
        }

        /// <summary>Whether this character is waiting in any mode.</summary>
        public static bool Waits(long characterId)
        {
            lock (_gate) return FindUnit(characterId, out _, out _) != null;
        }

        /// <summary>The oldest wait in a mode, or null when nobody waits there.</summary>
        public static TimeSpan? LongestWait(int mode)
        {
            lock (_gate)
            {
                if (!_byMode.TryGetValue(mode, out var units) || units.Count == 0) return null;
                return Clock() - units.Min(u => u.Since);
            }
        }

        /// <summary>Enrols one player. False if he was already waiting, anywhere.</summary>
        public static bool Enrol(long characterId, int mode) => EnrolUnit(new[] { characterId }, mode) > 0;

        /// <summary>
        /// Enrols players who will fight together, as one unit; those already waiting stay where
        /// they were. Returns how many were enrolled.
        /// </summary>
        public static int EnrolUnit(IReadOnlyList<long> characterIds, int mode)
        {
            var fresh = characterIds.Where(id => id != 0).Distinct().ToList();
            if (fresh.Count == 0) return 0;
            var profiles = fresh.ToDictionary(id => id, id => Describe(id, mode));

            lock (_gate)
            {
                fresh.RemoveAll(id => FindUnit(id, out _, out _) != null);
                if (fresh.Count == 0) return 0;
                if (!_byMode.TryGetValue(mode, out var units)) _byMode[mode] = units = new List<Unit>();
                var unit = new Unit { Sequence = ++_sequence, Since = Clock() };
                foreach (long id in fresh)
                    unit.Members.Add(new Waiting { CharacterId = id, Mode = mode, Profile = profiles[id] });
                units.Add(unit);
                return fresh.Count;
            }
        }

        /// <summary>Takes a player out of wherever he waits. Returns that mode, or -1.</summary>
        public static int Leave(long characterId)
        {
            lock (_gate)
            {
                var unit = FindUnit(characterId, out int mode, out var units);
                if (unit == null) return -1;
                unit.Members.RemoveAll(m => m.CharacterId == characterId);
                if (unit.Members.Count == 0) units!.Remove(unit);
                return mode;
            }
        }

        /// <summary>
        /// Takes out the whole unit a player waits in -- a party that enrolled together leaves
        /// together. Returns its mode and members, or -1 and nobody.
        /// </summary>
        public static (int Mode, List<long> Members) LeaveWithUnit(long characterId)
        {
            lock (_gate)
            {
                var unit = FindUnit(characterId, out int mode, out var units);
                if (unit == null) return (-1, new List<long>());
                units!.Remove(unit);
                return (mode, unit.Members.Select(m => m.CharacterId).ToList());
            }
        }

        /// <summary>
        /// A match, if the queue has one: two sides of <paramref name="teamSize"/>, out of the queue
        /// already, so that two calls at once cannot take the same player to two fights.
        /// </summary>
        public static (List<long> Blue, List<long> Red)? TryMatch(int mode, int teamSize)
        {
            if (teamSize <= 0) return null;
            lock (_gate)
            {
                if (!_byMode.TryGetValue(mode, out var units)) return null;
                if (units.Sum(u => u.Members.Count) < teamSize * 2) return null;
                var now = Clock();

                foreach (var anchor in units.OrderBy(u => u.Since).ThenBy(u => u.Sequence).ToList())
                {
                    if (anchor.Members.Count > teamSize) continue;
                    int window = RatingWindow(now - anchor.Since);
                    var picked = new List<Unit> { anchor };
                    int count = anchor.Members.Count;

                    var candidates = units.Where(u => u != anchor)
                                          .OrderBy(u => Math.Abs(u.Rating - anchor.Rating))
                                          .ThenBy(u => u.Since).ThenBy(u => u.Sequence);
                    foreach (var candidate in candidates)
                    {
                        if (count == teamSize * 2) break;
                        if (candidate.Members.Count > teamSize || count + candidate.Members.Count > teamSize * 2) continue;
                        if (!picked.All(p => Compatible(p, candidate, window))) continue;
                        picked.Add(candidate);
                        count += candidate.Members.Count;
                    }
                    if (count != teamSize * 2) continue;

                    var split = Split(picked, teamSize);
                    if (split == null) continue;
                    foreach (var unit in picked) units.Remove(unit);
                    return split;
                }
                return null;
            }
        }

        /// <summary>How far apart two ratings may be after waiting this long.</summary>
        public static int RatingWindow(TimeSpan waited)
            => (int)Math.Min(RatingWindowMax, RatingWindowBase + RatingWindowPerSecond * Math.Max(0, waited.TotalSeconds));

        /// <summary>Whether two players may be in the same fight, on either side.</summary>
        public static bool CanMeet(Profile a, Profile b, int ratingWindow)
        {
            int ratingGap = Math.Abs(a.Rating - b.Rating);
            if (ratingGap > ratingWindow) return false;
            if (a.Level <= 0 || b.Level <= 0) return true;
            if (Math.Abs(a.Level - b.Level) <= LevelGap) return true;
            return a.Placed && b.Placed && ratingGap <= LadderEven;
        }

        private static bool Compatible(Unit a, Unit b, int window)
            => a.Members.All(x => b.Members.All(y => CanMeet(x.Profile, y.Profile, window)));

        /// <summary>
        /// The two sides: whole units, <paramref name="teamSize"/> players each, their average
        /// ratings as close as can be; on a tie, the earliest to arrive go blue.
        /// </summary>
        private static (List<long> Blue, List<long> Red)? Split(List<Unit> picked, int teamSize)
        {
            var ordered = picked.OrderBy(u => u.Since).ThenBy(u => u.Sequence).ToList();
            int n = ordered.Count;
            (List<long>, List<long>)? best = null;
            double bestGap = double.MaxValue;
            for (int mask = 1; mask < (1 << n); mask++)
            {
                if ((mask & 1) == 0) continue;                  // the first unit goes blue
                var blue = new List<Waiting>();
                var red = new List<Waiting>();
                for (int i = 0; i < n; i++) ((mask >> i & 1) == 1 ? blue : red).AddRange(ordered[i].Members);
                if (blue.Count != teamSize || red.Count != teamSize) continue;
                double gap = Math.Abs(blue.Average(w => w.Profile.Rating) - red.Average(w => w.Profile.Rating));
                if (gap < bestGap)
                {
                    bestGap = gap;
                    best = (blue.Select(w => w.CharacterId).ToList(), red.Select(w => w.CharacterId).ToList());
                }
            }
            return best;
        }

        private static Unit? FindUnit(long characterId, out int mode, out List<Unit>? units)
        {
            foreach (var (m, list) in _byMode)
            {
                foreach (var unit in list)
                {
                    if (unit.Members.Any(w => w.CharacterId == characterId))
                    {
                        mode = m;
                        units = list;
                        return unit;
                    }
                }
            }
            mode = -1;
            units = null;
            return null;
        }

        private static Profile DescribeFromTheWorld(long characterId, int mode)
        {
            int level = Network.SessionRegistry.FindByCharacter(characterId)?.State.CharacterLevel
                        ?? DatabaseManager.GetCharacterById(characterId)?.Level ?? 0;
            var standing = KoliseoLadder.Of(characterId, mode, level);
            return new Profile(level, standing.Rating, standing.Placed);
        }

        /// <summary>For tests: empty queues and the world's own view of players.</summary>
        internal static void ForgetEverything()
        {
            lock (_gate) _byMode.Clear();
            Describe = DescribeFromTheWorld;
            Clock = () => DateTime.UtcNow;
        }
    }
}
