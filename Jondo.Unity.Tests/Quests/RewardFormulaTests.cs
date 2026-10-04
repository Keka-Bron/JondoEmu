using Jondo.Unity.World.Quests;
using Xunit;

namespace Jondo.Unity.Tests.Quests
{
    /// <summary>
    /// The base the reward ratios multiply, against every claim the captures pay out.
    /// </summary>
    /// <remarks>
    /// The formula is the client's own (class <c>lg</c> in Core, <c>nza</c> and <c>nzd</c>), and
    /// each case below is a kuf the real server sent, with the character's level, the reward's
    /// level and ratio, and the bonus those characters had: 5 %, and 110 % for the second
    /// character of the rat-hunt capture.
    /// </remarks>
    public class RewardFormulaTests
    {
        [Theory]
        // Tutorial capture: 8518, 8519, 8520, 120 and 424.
        [InlineData(1, 1, 0.213, 5, 115)]
        [InlineData(2, 2, 0.213, 5, 241)]
        [InlineData(2, 2, 0.287, 5, 325)]
        [InlineData(2, 200, 0.1, 5, 113)]
        [InlineData(2, 1, 0.05, 5, 27)]
        // Logros, the Pandala route and the guild hall: 8990, 8992, 8994 on an omega character.
        [InlineData(354, 1, 1.0, 5, 545)]
        // The rat hunt: 423 on another character of the same account.
        [InlineData(253, 1, 0.1, 110, 109)]
        public void Every_captured_claim_pays_what_the_real_server_paid(
            int playerLevel, int achievementLevel, double ratio, int bonus, long expected)
        {
            Assert.Equal(expected, RewardFormula.Experience(playerLevel, achievementLevel, ratio, 1.0, bonus));
        }

        [Fact]
        public void A_quest_step_uses_its_level_and_its_duration()
        {
            // Tutorial capture, frame 1871: quest 1629 (step 2249: level 2, duration 0.25, ratio
            // 0.5) pays 141 to a level-2 character.
            Assert.Equal(141, RewardFormula.Experience(2, 2, 0.5, 0.25, 5));
        }

        [Fact]
        public void The_halves_are_summed_before_they_are_rounded()
        {
            // The client truncates 0.3 and 0.7 of the base separately; with that rounding 424
            // would pay 26 and 8990 544. The captures say 27 and 545.
            Assert.Equal(26, RewardFormula.Experience(2, 1, 0.05));
            Assert.Equal(520, RewardFormula.Experience(200, 1, 1.0));
        }

        [Fact]
        public void The_kamas_of_the_tutorial_prototype_are_two()
        {
            // Achievement 120, kamasRatio 0.1 and scaling with the character's level: 2 kamas at
            // level 2, frame 1861.
            Assert.Equal(2, RewardFormula.Kamas(2, 0.1));
        }

        [Fact]
        public void Nothing_is_worth_nothing()
        {
            Assert.Equal(0, RewardFormula.Experience(100, 100, 0));
            Assert.Equal(0, RewardFormula.Kamas(100, 0));
        }

        [Fact]
        public void The_limit_keeps_only_what_the_character_accepts()
        {
            long full = RewardFormula.Experience(100, 100, 1.0);
            Assert.Equal(full / 2, RewardFormula.Experience(100, 100, 1.0, 1.0, 0, 50));
            Assert.Equal(0, RewardFormula.Experience(100, 100, 1.0, 1.0, 0, 0));
        }
    }
}
