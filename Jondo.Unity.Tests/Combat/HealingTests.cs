using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    public class HealingTests
    {
        [Theory]
        [InlineData(20, 0, 0, 20)]
        [InlineData(20, 200, 0, 60)]
        [InlineData(20, 200, 7, 67)]
        [InlineData(21, 50, 0, 31)]
        public void Fixed_healing_uses_intelligence_then_flat_heals(
            int baseHeal, int intelligence, int flatHeals, int expected)
            => Assert.Equal(expected,
                            EffectEngine.CalcularCuraFija(baseHeal, intelligence, flatHeals));

        [Theory]
        [InlineData(40, 0, 0, 50, 20)]
        [InlineData(40, 0, 0, 110, 44)]
        [InlineData(40, 0, 0, 0, 0)]
        public void Received_healing_multiplier_is_applied_last(
            int baseHeal, int intelligence, int flatHeals, int multiplier, int expected)
            => Assert.Equal(expected,
                            EffectEngine.CalcularCuraFija(
                                baseHeal, intelligence, flatHeals, multiplier));

        [Theory]
        [InlineData(20, -100, 5, 5)]
        [InlineData(20, -200, 0, 0)]
        [InlineData(20, 0, -25, 0)]
        [InlineData(0, 500, 500, 0)]
        public void Fixed_healing_never_becomes_negative(
            int baseHeal, int intelligence, int flatHeals, int expected)
            => Assert.Equal(expected,
                            EffectEngine.CalcularCuraFija(baseHeal, intelligence, flatHeals));

        [Fact]
        public void Fixed_healing_saturates_instead_of_overflowing()
            => Assert.Equal(int.MaxValue,
                            EffectEngine.CalcularCuraFija(
                                int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue));
    }
}
