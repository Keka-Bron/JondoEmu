using System;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The guild levels, 1 to 20, and the experience each one starts at.
    /// </summary>
    /// <remarks>
    /// The client has no such table: the server tells it the floor of the level, the experience
    /// and the floor of the next (jhh f5, f6 and f9). The curve is the DofusPourLesNoobs guilds
    /// guide's "Tableau d'expérience de guilde", and the captures agree wherever they reach: a level
    /// 1 guild with f9 = 50; a level 4 one with 150 / 181 / 210; level 5 with 210 / 225 / 270; level 7
    /// with 340 / 368 / 410.
    /// </remarks>
    public static class GuildLevels
    {
        public const int MaxLevel = 20;

        /// <summary>The experience each level starts at: [0] is level 1.</summary>
        private static readonly long[] Floors =
        {
            0, 50, 100, 150, 210, 270, 340, 410, 490, 570,
            660, 750, 860, 970, 1090, 1220, 1360, 1520, 1690, 1890,
        };

        public static long FloorOf(int level) => Floors[Math.Clamp(level, 1, MaxLevel) - 1];

        /// <summary>The experience the next level starts at; at the top, the top's own.</summary>
        public static long NextFloorOf(int level) => FloorOf(Math.Min(level + 1, MaxLevel));

        public static int LevelFor(long experience)
        {
            int level = 1;
            while (level < MaxLevel && experience >= FloorOf(level + 1)) level++;
            return level;
        }
    }
}
