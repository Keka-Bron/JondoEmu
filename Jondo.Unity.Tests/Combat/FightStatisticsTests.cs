using System.Linq;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The end-of-fight statistics (jxo), byte for byte against two captured ones. The only
    /// field left out is f11.f10, whose meaning is not measured; it is cut from the captured
    /// bytes, with the two lengths above it shortened by its two bytes.
    /// </summary>
    public class FightStatisticsTests
    {
        /// <summary>
        /// "ocra-flecha de retroceso", second fight: two arrows of 3 AP for 100 damage, one
        /// turn, no MP, nothing taken, one monster down.
        /// </summary>
        [Fact]
        public void Two_arrows_in_one_turn()
        {
            var s = new FightStatistics
            {
                DirectDamage = 100, ActionPointsSpent = 6, ActionPointsOnDamage = 6, TurnsPlayed = 1,
            };

            byte[] frame = FightProtocol.BuildFightStatistics(53721235555, s, enemiesDefeated: 1);

            Assert.Equal(
                "0a3d08e380a490c80112340a09120020e380a490c8011204100120011a05250000c04022002a00320042004a0052005a0e1d555585412064350000c84248641204080128" + "64",
                Hex(frame));
        }

        /// <summary>
        /// "tymador-tymobot": 345 of wall damage and 1,655 from the bombs and the bot over 28
        /// turns -- his 16 and the bot's 12 -- 82 AP, 21 MP, 1,400 of shields from Tymadura,
        /// not a point taken, four monsters down. No AP bought damage of his own, so the "per
        /// AP" is the 345 itself.
        /// </summary>
        [Fact]
        public void Bombs_walls_and_shields()
        {
            var s = new FightStatistics
            {
                GlyphDamage = 345, SummonDamage = 1655, ShieldsGiven = 1400,
                ActionPointsSpent = 82, MovementPointsSpent = 21, TurnsPlayed = 28,
            };

            byte[] frame = FightProtocol.BuildFightStatistics(53721497699, s, enemiesDefeated: 4);

            Assert.Equal(
                "0a4f08e380b490c80112460a09120020e380b490c8011204100420041a0525b76d3b4022002a00320808f80a1d000048424205250000403f4a0052005a1310f70c1d0080ac4320d00f356edb8e4238d9021208080428d00f40f80a",
                Hex(frame));
        }

        private static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));
    }
}
