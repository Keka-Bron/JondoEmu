using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Nagate (6256), in the Valley of the Water Lady. Her 16822 heals every monster by a share
    /// of its maximum life (1109 on "m" in a whole-map zone): 25% plainly, 38/50/63% under the
    /// states 1908, 1909 and 1910. "m" is her side's monsters but not herself: masks such as
    /// "c,h,m,d" and "m,c" name the caster beside it. Her own life does not come back.
    /// </summary>
    public class NagateFreshWaterTests
    {
        private const int Nagate = 6256, FreshWater = 16822;

        [Fact]
        public async Task Fresh_water_heals_her_side_but_not_her()
        {
            if (SpellEffects.De(FreshWater, 1).Count == 0) return;
            var fight = new FightInstance(1, 1);
            var player = new Fighter
            {
                Id = 10, TeamId = 0, CellId = 100, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            var boss = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 272, MaxHP = 6786, CurrentHP = 5752, Level = 130,
                IsMonster = true, MonsterId = Nagate, MaxAP = 12, CurrentAP = 12, MaxMP = 3, CurrentMP = 3,
            };
            var ally = new Fighter
            {
                Id = -2, TeamId = 1, CellId = 300, MaxHP = 4000, CurrentHP = 2000, Level = 130,
                IsMonster = true, MonsterId = 6234, MaxAP = 8, CurrentAP = 8, MaxMP = 3, CurrentMP = 3,
            };
            fight.AddPlayer(player);
            fight.AddMonster(boss);
            fight.AddMonster(ally);
            fight.StartFight();

            await FightHandler.AplicarEfectosAsync(null, fight, boss, FreshWater, 1, boss,
                                                   EffectEngine.AlLanzar, boss.CellId);

            Assert.Equal(3000, ally.CurrentHP);
            Assert.Equal(5752, boss.CurrentHP);
        }
    
        [Fact]
        public async Task Out_of_my_sight_sends_the_player_off_her_islet()
        {
            if (SpellEffects.De(16825, 1).Count == 0) return;
            var fight = new FightInstance(1, 1);
            var player = new Fighter
            {
                Id = 10, TeamId = 0, CellId = 300, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            var boss = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 301, MaxHP = 6786, CurrentHP = 6786, Level = 130,
                IsMonster = true, MonsterId = Nagate, MaxAP = 12, CurrentAP = 12, MaxMP = 3, CurrentMP = 3,
            };
            fight.AddPlayer(player);
            fight.AddMonster(boss);
            fight.StartFight();
            // Her Retirada leaves the Pesanteur (7) on whoever is on her islet.
            player.Buffs.PonerEstado(7);

            await FightHandler.AplicarEfectosAsync(null, fight, boss, 16825, 1, boss,
                                                   EffectEngine.AlLanzar, player.CellId);

            Assert.Equal(128, player.CellId);
        }
    }
}
