using System;
using System.Collections.Generic;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Profession experience: how much each character has and what level he is at.
    ///
    /// ─── The curve, which comes from three measured points ──────────────────────────────────
    ///
    /// The client does NOT bring a profession experience table. Its own, CharacterXpMappings, only has
    /// one column and it is the character's: 110 for level 2, 650 for level 3. Professions go a
    /// different way and that way does not travel in the data.
    ///
    /// But the captures' <c>irq</c> shows it by accident, because it sends TOTALS and not
    /// increments: f2 is what is needed for the next level, f3 the level, f4 the floor of the
    /// current level and f5 the accumulated experience. Three points come out of that:
    ///
    ///     level   2  ->      20     (farmer level-up, wheat capture)
    ///     level   3  ->      60     (the f2 of that same level-up)
    ///     level 200  -> 398,000     (the lumberjack's f4, who is at the cap)
    ///
    /// And the same formula fits all three:
    ///
    ///     experience(level) = 10 · level · (level − 1)
    ///
    ///     10·2·1 = 20 ✔      10·3·2 = 60 ✔      10·200·199 = 398,000 ✔
    ///
    /// Three out of three, the extreme included. It is not an invented curve passing through two points: it is
    /// a simple formula that hits every one there is, and the level 200 one is the one that would
    /// hardly come out by chance.
    ///
    /// ─── How much is earned ─────────────────────────────────────────────────────────────────
    ///
    /// Ten per harvest, and it is FIXED: it was measured with 20, 14 and 17 units of wood and all three times
    /// it was +10. It does not go by units.
    /// </summary>
    public static class JobExperience
    {
        /// <summary>The level a profession reaches.</summary>
        public const int MaxLevel = 200;

        /// <summary>What one harvest gives. Measured three times with different quantities.</summary>
        public const int PerGather = 10;

        /// <summary>The accumulated experience a level starts with.</summary>
        public static long Floor(int level)
        {
            if (level <= 1) return 0;
            if (level > MaxLevel) level = MaxLevel;
            return 10L * level * (level - 1);
        }

        /// <summary>What is needed for the next level, or zero if already at the cap.</summary>
        public static long Next(int level) => level >= MaxLevel ? 0 : Floor(level + 1);

        /// <summary>What level someone with this experience is at.</summary>
        public static int LevelOf(long experience)
        {
            if (experience < 20) return 1;
            // It is solved from 10·n·(n−1) and then adjusted by hand, which is shorter than iterating
            // two hundred times and does not trust floating-point arithmetic for the edge.
            int level = (int)Math.Floor((1 + Math.Sqrt(1 + 0.4 * experience)) / 2);
            if (level < 1) level = 1;
            if (level > MaxLevel) level = MaxLevel;
            while (level < MaxLevel && Floor(level + 1) <= experience) level++;
            while (level > 1 && Floor(level) > experience) level--;
            return level;
        }

        /// <summary>What a character has in a profession.</summary>
        public sealed class Progress
        {
            public int JobId { get; init; }
            public long Experience { get; set; }
            public int Level => LevelOf(Experience);
        }

        /// <summary>
        /// Adds experience and says whether it levelled up.
        ///
        /// The state lives in the character's session, which is what stores it in the database.
        /// </summary>
        public static bool Add(IDictionary<int, Progress> jobs, int jobId, long amount,
                               out Progress progress)
        {
            if (!jobs.TryGetValue(jobId, out progress!))
            {
                progress = new Progress { JobId = jobId, Experience = 0 };
                jobs[jobId] = progress;
            }

            int before = progress.Level;
            progress.Experience += amount;
            if (progress.Experience > Floor(MaxLevel)) progress.Experience = Floor(MaxLevel);
            return progress.Level > before;
        }

        /// <summary>
        /// What one craft gives: 20 a level of the recipe, less the further the job is above it.
        /// </summary>
        /// <remarks>
        /// The only craft in the captures is the tutorial's ring, a level-1 recipe crafted at job
        /// level 1: +20, which is exactly what takes the job to level 2. The rest is the formula
        /// players measured on the official server and posted on its forum (2021):
        ///
        ///     xp = ⌊20 · recipe level / (1 + 0.1 · (job level − recipe level)^1.1)⌋
        ///
        /// which gives 20 there. A recipe above the job's level cannot be crafted at all.
        /// </remarks>
        public static int Craft(int jobLevel, int recipeLevel)
        {
            if (recipeLevel <= 0) return 0;
            int gap = Math.Max(0, jobLevel - recipeLevel);
            return (int)Math.Floor(20.0 * recipeLevel / (1 + 0.1 * Math.Pow(gap, 1.1)));
        }

        /// <summary>
        /// What a rune that enters gives the magus: the item's level, with the same fall-off.
        /// </summary>
        /// <remarks>
        /// Measured only at the top: in the two smithmagic captures a level-200 magus gets +1 for
        /// each of the 83 runes that entered, on items of level 7, 10 and 44, and nothing for the
        /// ones that failed. The item's level through the craft fall-off gives 1 for all three; a
        /// magus of the item's own level gets the item's level.
        /// </remarks>
        public static int Magus(int jobLevel, int itemLevel)
        {
            int gap = Math.Max(0, jobLevel - itemLevel);
            return Math.Max(1, (int)Math.Floor(Math.Max(1, itemLevel) / (1 + 0.1 * Math.Pow(gap, 1.1))));
        }
    }
}
