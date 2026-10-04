using System;

namespace Jondo.Unity.World.Quests
{
    /// <summary>
    /// What a reward's experience and kamas ratios are ratios OF: the base a quest step or an
    /// achievement multiplies.
    /// </summary>
    /// <remarks>
    /// <b>Read off the client, and checked against the captures.</b> The client shows the reward
    /// of an achievement and of a quest step before handing it over, so the formula is in its code:
    /// class <c>lg</c> in Core, methods <c>nza</c> (experience) and <c>nzd</c> (kamas), called by
    /// the achievement wrapper <c>od</c> and by the quest wrappers alike. Disassembled, with the
    /// constants read out of GameAssembly.dll:
    ///
    /// <code>
    ///   fixed(L)  = L * trunc((100 + 2L)^2) / 20 * duration * ratio         (float, then double)
    ///   L &lt;= A    experience = fixed(L)
    ///   L &gt; A     experience = 0.3 * fixed(A) + 0.7 * fixed(min(L, trunc(1.5 * A)))
    ///   then      * (1 + bonus / 100), * clamp(limit / 100, 0, 1)
    ///
    ///   kamas     = trunc(level^2 + 20 * level - 20) * ratio * duration
    /// </code>
    ///
    /// L is the character's level capped at 200, A the achievement's level or the step's optimal
    /// level, duration is 1 for an achievement and the step's own for a quest.
    ///
    /// Measured against eight claims and one finished quest in the captures, all exact:
    ///
    /// <code>
    ///   level 1, achievement 8518 (level 1, ratio 0.213)   115     tutorial
    ///   level 2, achievement 8519 (level 2, ratio 0.213)   241
    ///   level 2, achievement 8520 (level 2, ratio 0.287)   325
    ///   level 2, achievement 120  (level 200, ratio 0.1)   113     and 2 kamas
    ///   level 2, achievement 424  (level 1, ratio 0.05)    27
    ///   level 2, quest 1629 step 2249 (level 2, 0.5, 0.25) 141
    ///   200+, achievements 8990, 8992, 8994 (1, ratio 1)   545     three captures
    ///   200+, achievement 423 (level 1, ratio 0.1)         109     another character
    /// </code>
    ///
    /// with a bonus of 5 % for every character but the last, which had 110 % — the bonus comes
    /// from the character, not from the reward, and this emulator does not model it.
    ///
    /// <b>Two places where the server's rounding is not the client's.</b> The client truncates
    /// the 0.3 and the 0.7 halves separately, and multiplies by the bonus in float. With that
    /// rounding 424 would pay 26 and 8990 544 or 546, and the captures say 27 and 545. Summing the
    /// halves before truncating, and taking the product of the float multiplier wider than a
    /// float, every one of the nine comes out exact.
    /// </remarks>
    public static class RewardFormula
    {
        /// <summary>The level the formula stops at: past it, omega levels change nothing.</summary>
        public const int LevelCap = 200;

        /// <summary>The experience a reward is worth to a character of that level.</summary>
        /// <param name="playerLevel">The character's level, omega levels included.</param>
        /// <param name="rewardLevel">The achievement's level, or the step's optimal level.</param>
        /// <param name="ratio">The reward's experience ratio.</param>
        /// <param name="duration">The step's duration; 1 for an achievement.</param>
        /// <param name="bonusPercent">The character's experience bonus, in percent.</param>
        /// <param name="limitPercent">How much of what is earned the character keeps.</param>
        public static long Experience(int playerLevel, int rewardLevel, double ratio,
                                      double duration = 1.0, int bonusPercent = 0,
                                      int limitPercent = 100)
        {
            if (ratio <= 0 || duration <= 0) return 0;

            int level = Math.Clamp(playerLevel, 1, LevelCap);
            int target = Math.Max(1, rewardLevel);

            long total;
            if (level <= target)
            {
                total = Fixed(level, ratio, duration);
            }
            else
            {
                int reach = Math.Min(level, (int)(target * 1.5f));
                double blended = 0.3 * Fixed(target, ratio, duration) + 0.7 * Fixed(reach, ratio, duration);

                // A hair over, so that 0.3x + 0.7x of a whole number lands on it and not under it.
                total = (long)Math.Floor(blended + 1e-7);
            }

            // The multiplier in float, as the client builds it, and the product wider than that:
            // 520 * 1.05f is 545.99997, which the captures pay as 545 and a float product would
            // round up to 546.
            double withBonus = total * (double)(1f + bonusPercent / 100f);
            long experience = (long)withBonus;

            float kept = Math.Clamp(limitPercent / 100f, 0f, 1f);
            return (long)Math.Max(0.0, experience * (double)kept);
        }

        /// <summary>
        /// The kamas a reward is worth. <paramref name="level"/> is the character's level when the
        /// reward scales with it, and the achievement's own level when it does not.
        /// </summary>
        /// <remarks>
        /// Capped at 200 like the experience. The client reads the character's level through a
        /// getter here and no capture claims kamas on an omega character, so whether the server
        /// caps it too is not measured.
        /// </remarks>
        public static long Kamas(int level, double ratio, double duration = 1.0)
        {
            if (ratio <= 0 || duration <= 0) return 0;

            int l = Math.Clamp(level, 1, LevelCap);
            int basis = (int)(MathF.Pow(l, 2f) + 20 * l - 20f);
            return (long)(basis * ratio * duration);
        }

        /// <summary>
        /// The fixed part, with the client's own mix of float and double: the square is taken in
        /// float and truncated, divided in float, and only then multiplied in double.
        /// </summary>
        private static long Fixed(int level, double ratio, double duration)
        {
            int square = (int)MathF.Pow(100 + 2 * level, 2f);
            float perLevel = level * square / 20f;
            return (long)((double)perLevel * duration * ratio);
        }
    }
}
