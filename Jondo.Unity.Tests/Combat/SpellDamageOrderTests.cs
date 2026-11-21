using Jondo.Unity.Server.Handlers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A blow's damage in the order and with the roundings of the client's own fight preview
    /// (DamageCalculator names the functions, read in the 3.6.10.11 client).
    /// </summary>
    public class SpellDamageOrderTests
    {
        private static int Blow(int roll, int stat = 0, int power = 0, int flat = 0, int resistance = 0,
                                int reduction = 0, int percentBonus = 0, int weaponPercent = 0)
            => DamageCalculator.CalculateDamage(roll, ElementType.Fire, stat, power, 0, flat, resistance, reduction,
                                                percentBonus: percentBonus, weaponDamagePercent: weaponPercent);

        /// <summary>
        /// 1 - 0.01f × 47 is a hair under 0.53 in single precision: a blow of 100 leaves 52, where
        /// the exact product would give 53.
        /// </summary>
        [Fact]
        public void The_resistance_rounds_down_in_single_precision()
        {
            Assert.Equal(52, Blow(100, resistance: 47));
            Assert.Equal(7, Blow(10, resistance: 30));
            Assert.Equal(50, Blow(100, resistance: 50));
        }

        /// <summary>The flat reduction goes off before the percentage: (100 - 20) × 50 % = 40, not 30.</summary>
        [Fact]
        public void The_flat_reduction_goes_before_the_percentage()
        {
            Assert.Equal(40, Blow(100, resistance: 50, reduction: 20));
        }

        /// <summary>
        /// The characteristic, power and the spells' % bonus (98) make one percentage, floored once:
        /// 33 × (100 + 100 + 50 + 20) % = 89.1, so 89; then the flat ones on top.
        /// </summary>
        [Fact]
        public void The_characteristic_power_and_the_percent_bonus_add_up_before_the_flat_ones()
        {
            Assert.Equal(89, Blow(33, stat: 100, power: 50, percentBonus: 20));
            Assert.Equal(99, Blow(33, stat: 100, power: 50, percentBonus: 20, flat: 10));
        }

        /// <summary>A sum at or below zero leaves the roll as it is: the client only multiplies above zero.</summary>
        [Fact]
        public void A_negative_sum_leaves_the_roll_alone()
        {
            Assert.Equal(30, Blow(30, stat: -50, power: 20));
        }

        /// <summary>The weapon damage percent (31) multiplies the roll and its flat ones: (20 + 10) × 150 % = 45.</summary>
        [Fact]
        public void The_weapon_damage_percent_takes_the_flat_ones_in()
        {
            Assert.Equal(45, Blow(20, flat: 10, weaponPercent: 50));
        }

        /// <summary>A value times a percentage floors the float product (ra.fay).</summary>
        [Theory]
        [InlineData(10, 110, 11)]
        [InlineData(99, 115, 113)]
        [InlineData(7, 50, 3)]
        public void Times_percent_floors(int value, int percent, int expected)
        {
            Assert.Equal(expected, DamageCalculator.TimesPercent(value, percent));
        }

        /// <summary>The target's reduction is its element's, 54 to 58, with the rows on him.</summary>
        [Fact]
        public void The_flat_reduction_is_the_element_s()
        {
            var target = new Fighter { Id = 2, MaxHP = 100, CurrentHP = 100 };
            target.Otras[55] = 12;   // FireElementReduction
            target.Otras[54] = 40;   // EarthElementReduction
            Assert.Equal(12, FightHandler.ReduccionFijaDeLaVictima(target, ElementType.Fire, 1));
            Assert.Equal(40, FightHandler.ReduccionFijaDeLaVictima(target, ElementType.Earth, 1));
            Assert.Equal(0, FightHandler.ReduccionFijaDeLaVictima(target, ElementType.Air, 1));
        }
    }
}
