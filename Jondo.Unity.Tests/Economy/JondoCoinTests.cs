using Jondo.Unity.Launcher.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Economy
{
    /// <summary>
    /// The Jondo Coin pays by bands of 25 monster levels.
    /// </summary>
    /// <remarks>
    /// The edges are what breaks on its own when somebody touches the formula: level 25 has to pay
    /// one and level 26 two, not the other way round. A "/ 25" without the "- 1" in front moves
    /// exactly those two, and nobody notices until a level-25 monster drops two coins.
    /// </remarks>
    public class JondoCoinTests
    {
        [Theory]
        [InlineData(1, 1)]
        [InlineData(25, 1)]     // last of the first band
        [InlineData(26, 2)]     // first of the second
        [InlineData(50, 2)]
        [InlineData(51, 3)]
        [InlineData(75, 3)]
        [InlineData(176, 8)]
        [InlineData(200, 8)]
        [InlineData(201, 9)]    // the last band
        [InlineData(225, 9)]
        public void Every_band_edge_pays_what_it_should(int level, int coins)
        {
            Assert.Equal(coins, JondoCoin.RewardFor(level));
        }

        [Theory]
        [InlineData(226)]
        [InlineData(2400)]
        [InlineData(int.MaxValue)]
        public void Above_the_last_band_the_reward_is_capped(int level)
        {
            Assert.Equal(9, JondoCoin.RewardFor(level));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-40)]
        [InlineData(int.MinValue)]
        public void A_nonsensical_level_still_pays_at_least_one_coin(int level)
        {
            // Never zero: a fight would then put a stack of nothing in the inventory and the client
            // would paint an empty pile.
            Assert.True(JondoCoin.RewardFor(level) >= 1);
        }

        [Fact]
        public void The_reward_never_goes_down_as_the_level_goes_up()
        {
            // A property rather than a table: whatever the formula becomes, it has to stay
            // monotonic, or a harder monster would pay less than an easier one.
            int previous = JondoCoin.RewardFor(1);
            for (int level = 2; level <= 300; level++)
            {
                int now = JondoCoin.RewardFor(level);
                Assert.True(now >= previous, $"level {level} pays {now}, less than level {level - 1}'s {previous}");
                previous = now;
            }
        }

        [Fact]
        public void The_band_width_and_the_ceiling_are_what_the_formula_assumes()
        {
            // The table above is written against these two numbers. If either moves the table is
            // stale, and this says so instead of letting the edges quietly drift.
            Assert.Equal(25, JondoCoin.LevelsPerBand);
            Assert.Equal(225, JondoCoin.HighestBandedLevel);
        }
    }
}
