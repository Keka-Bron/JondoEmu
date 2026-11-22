using Jondo.Unity.Server.Handlers;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>What counts as being hit by an enemy, for the hooks that wait on it (DBE).</summary>
    public class BlowTests
    {
        [Fact]
        public void A_blow_the_resistances_swallow_whole_is_still_a_blow()
        {
            // The Minotauroro at 100% in every element: nothing reaches his life, and his Choque,
            // which takes 10% off those resistances, waits on exactly this blow.
            Assert.True(FightHandler.IsABlow(applied: 0, computed: 0, byAnEnemy: true));
        }

        [Fact]
        public void A_blow_that_lands_is_one()
            => Assert.True(FightHandler.IsABlow(applied: 120, computed: 120, byAnEnemy: true));

        [Fact]
        public void A_blow_a_shield_takes_whole_is_not()
            => Assert.False(FightHandler.IsABlow(applied: 0, computed: 300, byAnEnemy: true));

        [Fact]
        public void An_ally_never_hits_for_this()
        {
            Assert.False(FightHandler.IsABlow(applied: 120, computed: 120, byAnEnemy: false));
            Assert.False(FightHandler.IsABlow(applied: 0, computed: 0, byAnEnemy: false));
        }
    }
}
