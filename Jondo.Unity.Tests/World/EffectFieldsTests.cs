using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    public sealed class EffectFieldsTests
    {
        [Fact]
        public void A_resolved_range_uses_its_value_when_both_dice_are_zero()
        {
            EffectFields.Initialize();

            Assert.Equal(
                (EffectFields.AsRange, 44L, 44L, 0L),
                EffectFields.Shape(2822, value: 44, diceNum: 0, diceSide: 0));

            Assert.Equal(
                (EffectFields.AsRange, 20L, 10L, 0L),
                EffectFields.Shape(2822, value: 0, diceNum: 10, diceSide: 20));
        }
    }
}
