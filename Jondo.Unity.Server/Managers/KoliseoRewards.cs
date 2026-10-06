using System.Collections.Generic;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What the winner of a koliseo is paid.
    /// </summary>
    /// <remarks>
    /// Four things, and all four come from the end-of-fight jyg of the capture of the complete
    /// koliseo —two versus two, with kolichas handed out—. The jyg's four entries:
    ///
    /// <code>
    ///   WIN (they carry f4 = 2)
    ///     level 227   3,400 kamas   260 × 12736   2 × 34478   4,722,600 experience
    ///     level 290   2,800 kamas   230 × 12736   2 × 34478   7,496,344 experience
    ///   LOSE
    ///     level 354   zero-byte loot, and the experience block WITHOUT f1
    ///     level 447   the same
    /// </code>
    ///
    /// From that comes, MEASURED: that it pays, what it pays, and that the loser gets nothing —not even
    /// experience; his block goes without the field for what was earned, not with a zero—.
    ///
    /// What does NOT come from there is the FORMULA, and it is worth saying plainly: there are two winners, that is two
    /// points. And the two points do not even go the way one would expect —the level 290 one
    /// gets FEWER kamas and FEWER kolichas than the 227 one—, so not even with the best will
    /// can a function of the level be drawn from here. Both beat the same two rivals, of
    /// level 354 and 447, so it is not the rival's level that separates them either. It looks like a
    /// bonus for being the one with the lowest level, but with two numbers that is a hunch, not a
    /// measurement.
    ///
    /// So the kamas, the kolichas and the vitorichas are CONSTANTS, and the constant is the mean
    /// of what was measured. It is a decision, not a finding, and it is here in three numbers so that changing it
    /// the day there are more captures is changing three numbers.
    ///
    /// The experience does allow something better than a constant. Placed over the level's band
    /// —what goes from the level's floor to the next one's— the two winners fall almost in the same
    /// place:
    ///
    /// <code>
    ///   227   4,722,600 of 65,410,444    7.22 %
    ///   290   7,496,344 of 122,431,633   6.12 %
    /// </code>
    ///
    /// Two points a little more than one percentage point apart. 6.67 % is used, which is the mean,
    /// and with that the figure comes out reasonable at any level instead of being ridiculous at the bottom and ridiculous
    /// at the top, which is what would happen with a constant.
    /// </remarks>
    public static class KoliseoRewards
    {
        /// <summary>The Kolicha. The loot's f4 in the two winning entries.</summary>
        public const int Kolicha = 12736;

        /// <summary>The Vitoricha, the koliseo's other currency.</summary>
        public const int Vitoricha = 34478;

        /// <summary>Mean of the two measured winners, 260 and 230.</summary>
        public const int KolichasPorVictoria = 245;

        /// <summary>The two measured winners take two. There is no mean to work out here.</summary>
        public const int VitorichasPorVictoria = 2;

        /// <summary>Mean of the two measured winners, 3,400 and 2,800.</summary>
        public const int KamasPorVictoria = 3100;

        /// <summary>What part of the level's band the winner takes, in ten-thousandths.</summary>
        /// <remarks>
        /// 667 out of 10,000 is 6.67 %: the mean of the 7.22 % and 6.12 % measured. In ten-thousandths and
        /// not in floating point so that the arithmetic is integer from start to finish and two servers
        /// with the same version pay exactly the same.
        /// </remarks>
        public const long ParteDeLaBanda = 667;

        /// <summary>What the winner is paid in items.</summary>
        public static Dictionary<int, int> Botin() => new Dictionary<int, int>
        {
            [Kolicha] = KolichasPorVictoria,
            [Vitoricha] = VitorichasPorVictoria,
        };

        /// <summary>
        /// The experience for winning, for a character of that level.
        /// </summary>
        /// <remarks>
        /// From the band of HIS level, not the rival's: in the capture the two winners are each paid
        /// on their own, and they are very different levels —227 and 290— against the same two
        /// rivals.
        /// </remarks>
        public static long Experiencia(int nivel)
        {
            long suelo = ExperienceTable.LevelFloor(nivel);
            long siguiente = ExperienceTable.NextLevelFloor(nivel);

            long banda = siguiente - suelo;
            if (banda <= 0) return 0;

            return banda * ParteDeLaBanda / 10000;
        }
    }
}
