using System.Collections.Generic;
using Jondo.Unity.Server.Handlers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>The master's heroes at a dungeon's door: a way in for each of them out of the group's bags, or nobody.</summary>
    public class DungeonHeroesTests
    {
        private static DungeonHandler.Member M(long id, int keys, bool keyring = false) => new(id, keyring, keys);

        [Fact]
        public void Each_hero_goes_in_on_his_own_key_first()
        {
            var plan = DungeonHandler.PlanForHeroes(M(1, 5), new[] { M(2, 1), M(3, 1) }, out int missing);
            Assert.Equal(0, missing);
            Assert.Equal(new Dictionary<long, long> { [2] = 2, [3] = 3 }, plan);
        }

        [Fact]
        public void Heroes_without_a_key_take_the_masters_then_another_heroes_spare()
        {
            // One of four heroes has his key: the three others take two of the master's and the third's spare.
            var plan = DungeonHandler.PlanForHeroes(M(1, 2), new[] { M(2, 0), M(3, 0), M(4, 2), M(5, 0) }, out int missing);
            Assert.Equal(0, missing);
            Assert.Equal(1, plan![2]);
            Assert.Equal(1, plan[3]);
            Assert.Equal(4, plan[4]);
            Assert.Equal(4, plan[5]);
        }

        [Fact]
        public void A_hero_with_his_keyring_spends_no_key()
        {
            var plan = DungeonHandler.PlanForHeroes(M(1, 0), new[] { M(2, 3, keyring: true), M(3, 0) }, out int missing);
            Assert.Equal(0, missing);
            Assert.Equal(0, plan![2]);
            Assert.Equal(2, plan[3]);
        }

        /// <summary>
        /// A hero whose quest wants the key handed to the guardian (Tassa's laboratory) gives one
        /// rather than use his keyring -- his own, else the group's --, and uses the keyring only when
        /// the group has none left. He went in by his keyring with his key in his bag, and his quest
        /// stayed behind his master's (2026-11-21, through the MCP).
        /// </summary>
        [Fact]
        public void A_hero_whose_quest_wants_the_key_gives_one_rather_than_use_his_keyring()
        {
            var own = DungeonHandler.PlanForHeroes(M(1, 0), new[] { new DungeonHandler.Member(2, true, 1, WantsKey: true) }, out int missing);
            Assert.Equal(0, missing);
            Assert.Equal(2, own![2]);

            var masters = DungeonHandler.PlanForHeroes(M(1, 1), new[] { new DungeonHandler.Member(2, true, 0, WantsKey: true) }, out missing);
            Assert.Equal(1, masters![2]);

            var none = DungeonHandler.PlanForHeroes(M(1, 0), new[] { new DungeonHandler.Member(2, true, 0, WantsKey: true) }, out missing);
            Assert.Equal(0, missing);
            Assert.Equal(0, none![2]);

            // Without a keyring and with no key in the group he is short, as anybody.
            Assert.Null(DungeonHandler.PlanForHeroes(M(1, 0), new[] { new DungeonHandler.Member(2, false, 0, WantsKey: true) }, out missing));
            Assert.Equal(1, missing);
        }

        [Fact]
        public void Short_of_keys_nobody_goes_in_and_the_shortfall_is_counted()
        {
            var plan = DungeonHandler.PlanForHeroes(M(1, 1), new[] { M(2, 0), M(3, 0), M(4, 0) }, out int missing);
            Assert.Null(plan);
            Assert.Equal(2, missing);
        }
    }
}
