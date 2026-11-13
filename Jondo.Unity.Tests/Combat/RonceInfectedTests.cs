using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Sadida's Ronce on an enemy «applies its effects to every infected enemy as well».
    /// </summary>
    [Collection("MapManager")]
    public class RonceInfectedTests
    {
        private const int Ronce = 13516, Infected = 263;

        private static (FightInstance Fight, Fighter Sadida, Fighter Aimed, Fighter Other) Fight(int aimedLife)
        {
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            var fight = new FightInstance(9_999_801, 0);
            var sadida = new Fighter { Id = 7401, Breed = 10, TeamId = 0, CellId = 300, MaxHP = 500, CurrentHP = 500, Level = 10, MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3 };
            var aimed = new Fighter { Id = -2, TeamId = 1, CellId = 302, MaxHP = 200, CurrentHP = aimedLife, Level = 10, IsMonster = true };
            var other = new Fighter { Id = -3, TeamId = 1, CellId = 330, MaxHP = 200, CurrentHP = 200, Level = 10, IsMonster = true };
            fight.AddPlayer(sadida);
            fight.AddMonster(aimed);
            fight.AddMonster(other);
            fight.StartFight();
            aimed.Buffs.PonerEstado(Infected);
            other.Buffs.PonerEstado(Infected);
            return (fight, sadida, aimed, other);
        }

        private static Task Ronce1(FightInstance fight, Fighter sadida, Fighter aimed)
            => FightHandler.CastEffectsAsync(null, fight, sadida, Ronce, 1, aimed, aimed.CellId, false,
                                             EffectEngine.EfectosSorteados(Ronce, 1, false));

        [Fact]
        public async Task On_an_infected_enemy_it_hits_the_other_infected_too()
        {
            var (fight, sadida, aimed, other) = Fight(200);
            await Ronce1(fight, sadida, aimed);
            Assert.True(aimed.CurrentHP < 200);
            Assert.True(other.CurrentHP < 200, "the other infected enemy took nothing");
        }

        [Fact]
        public async Task Killing_the_aimed_one_still_hits_the_other_infected()
        {
            var (fight, sadida, aimed, other) = Fight(1);
            await Ronce1(fight, sadida, aimed);
            Assert.False(aimed.IsAlive);
            Assert.True(other.CurrentHP < 200, $"the other infected enemy took nothing; sadida states {string.Join(",", sadida.Buffs.Estados)}, " +
                                               $"aimed states {string.Join(",", aimed.Buffs.Estados)}, other {string.Join(",", other.Buffs.Estados)}");
        }
    }
}
