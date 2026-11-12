using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A Steamer's turret fights with his characteristics on top of its own: a water Steamer's
    /// arponera previewed and struck its Espadon in earth, its grade's even 100 a tie.
    /// </summary>
    public class SteamerTurretStatsTests
    {
        private const int Arponera = 5836;

        [Fact]
        public void A_turret_carries_its_Steamer_s_characteristics()
        {
            var recipe = Summons.De(Arponera, 1);
            if (recipe.Plantilla != Arponera) return;   // no monster data
            var steamer = new Fighter { Id = 10, Level = 63, Strength = 110, Intelligence = 110, Chance = 1878, Agility = 110 };
            var turret = new Fighter { Id = -1, MonsterId = Arponera };

            Summons.Characteristics(turret, recipe, steamer);
            var alone = new Fighter { Id = -2, MonsterId = Arponera };
            Summons.Characteristics(alone, recipe, new Fighter { Id = 11, Level = 63 });

            Assert.Equal(alone.Chance + 1878, turret.Chance);
            Assert.Equal(alone.Strength + 110, turret.Strength);
            Assert.True(turret.Chance > turret.Strength && turret.Chance > turret.Intelligence && turret.Chance > turret.Agility);
        }

        [Fact]
        public void Another_summon_does_not()
        {
            var recipe = Summons.De(3958, 1);   // a beacon
            var steamer = new Fighter { Id = 10, Level = 63, Chance = 1878 };
            var beacon = new Fighter { Id = -1, MonsterId = 3958 };
            var alone = new Fighter { Id = -2, MonsterId = 3958 };
            Summons.Characteristics(beacon, recipe, steamer);
            Summons.Characteristics(alone, recipe, new Fighter { Id = 11, Level = 63 });
            Assert.Equal(alone.Chance, beacon.Chance);
        }
    }
}
