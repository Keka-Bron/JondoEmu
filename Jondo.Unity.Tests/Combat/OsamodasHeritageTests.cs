using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// An Osamodas' creature inherits its Osamodas' characteristics by its cost in PI -- «1 PI =
    /// 50% ; 2 PI = 75% ; 3 PI = 100%» -- in its family's characteristic and elemental damage,
    /// and in the power, damages and push damages of all. A level 200 Osamodas' Bouftou struck
    /// with its own 50 strength alone.
    /// </summary>
    public class OsamodasHeritageTests
    {
        private const int Bouftou = 8072, BigBouftou = 8078, Tofu = 8070;

        private static Fighter Osamodas() => new Fighter
        {
            Id = 10, Level = 200, Strength = 800, Agility = 400, Intelligence = 100,
            Power = 120, FlatDamage = 40, EarthDamage = 60, AirDamage = 20, PushDamage = 30,
        };

        private static (Fighter WithHim, Fighter Alone) Summon(int template, int grade)
        {
            var recipe = Summons.De(template, grade);
            var withHim = new Fighter { Id = -1, MonsterId = template };
            Summons.Characteristics(withHim, recipe, Osamodas());
            var alone = new Fighter { Id = -2, MonsterId = template };
            Summons.Characteristics(alone, recipe, new Fighter { Id = 11, Level = 200 });
            return (withHim, alone);
        }

        [Theory]
        [InlineData(1, 50)]
        [InlineData(2, 75)]
        [InlineData(3, 100)]
        [InlineData(0, 0)]
        public void The_share_follows_the_cost(int cost, int share)
            => Assert.Equal(share, Summons.OsamodasShare(cost));

        [Fact]
        public void A_bouftou_takes_half_of_its_osamodas_strength_and_earth()
        {
            var recipe = Summons.De(Bouftou, 3);
            if (recipe == null || recipe.Raza != Summons.OsamodasRace) return;   // no monster data
            var (bouftou, alone) = Summon(Bouftou, 3);

            Assert.Equal(alone.Strength + 400, bouftou.Strength);
            Assert.Equal(alone.EarthDamage + 30, bouftou.EarthDamage);
            Assert.Equal(alone.Power + 60, bouftou.Power);
            Assert.Equal(alone.FlatDamage + 20, bouftou.FlatDamage);
            Assert.Equal(alone.PushDamage + 15, bouftou.PushDamage);
            // Not the Tofu's agility, nor the air damage.
            Assert.Equal(alone.Agility, bouftou.Agility);
            Assert.Equal(alone.AirDamage, bouftou.AirDamage);
        }

        [Fact]
        public void A_three_pi_bouftou_takes_all_of_it()
        {
            var recipe = Summons.De(BigBouftou, 1);
            if (recipe == null || recipe.Raza != Summons.OsamodasRace) return;
            var (bouftou, alone) = Summon(BigBouftou, 1);
            Assert.Equal(alone.Strength + 800, bouftou.Strength);
            Assert.Equal(alone.Power + 120, bouftou.Power);
        }

        [Fact]
        public void A_tofu_takes_agility_and_air()
        {
            var recipe = Summons.De(Tofu, 3);
            if (recipe == null || recipe.Raza != Summons.OsamodasRace) return;
            var (tofu, alone) = Summon(Tofu, 3);
            Assert.Equal(alone.Agility + 200, tofu.Agility);
            Assert.Equal(alone.AirDamage + 10, tofu.AirDamage);
            Assert.Equal(alone.Strength, tofu.Strength);
        }

        /// <summary>Another class's summon takes nothing of this: a Cra's beacon.</summary>
        [Fact]
        public void Another_summon_does_not()
        {
            var recipe = Summons.De(3958, 1);
            if (recipe == null) return;
            var beacon = new Fighter { Id = -1, MonsterId = 3958 };
            var alone = new Fighter { Id = -2, MonsterId = 3958 };
            Summons.Characteristics(beacon, recipe, Osamodas());
            Summons.Characteristics(alone, recipe, new Fighter { Id = 11, Level = 200 });
            Assert.Equal(alone.Power, beacon.Power);
            Assert.Equal(alone.FlatDamage, beacon.FlatDamage);
        }
    }
}
