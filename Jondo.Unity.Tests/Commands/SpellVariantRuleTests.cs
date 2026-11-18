using Jondo.Unity.Server.Handlers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Commands
{
    /// <summary>
    /// A spell's variant is chosen out of a fight, or in one while placing: once the fight has
    /// started, it is what the character fights with. Accepted at any time, a Xelor swapped spells
    /// between two of his casts.
    /// </summary>
    public class SpellVariantRuleTests
    {
        [Theory]
        [InlineData(false, null, true)]
        [InlineData(true, FightState.Placement, true)]
        [InlineData(true, FightState.Ongoing, false)]
        [InlineData(true, FightState.Ended, false)]
        [InlineData(true, null, false)]
        public void Variants_are_chosen_out_of_a_fight_or_while_placing(bool inFight, FightState? fight, bool may)
            => Assert.Equal(may, SpellHandler.MayChangeVariants(inFight, fight));
    }
}
