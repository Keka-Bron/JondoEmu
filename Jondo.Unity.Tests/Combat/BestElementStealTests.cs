using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// «Roba vida con el mejor elemento» (2828): Concentración de Chakra's 24003, which goes off
    /// when its target suffers a trap's damage. It went to the buff panel unapplied, as did every
    /// 2828 of the monsters', because it was not counted as damage.
    /// </summary>
    public class BestElementStealTests
    {
        [Fact]
        public async Task A_best_element_steal_hits_and_heals_its_caster()
        {
            Assert.True(EffectEngine.EsDeDano(2828));
            Assert.True(EffectEngine.EsDeDano(2890));

            var fight = new FightInstance(1, 1);
            var sram = new Fighter { Id = 1, TeamId = 0, CellId = 300, MaxHP = 5000, CurrentHP = 4000, Level = 200,
                                     MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            var enemy = new Fighter { Id = -1, TeamId = 1, CellId = 301, MaxHP = 5000, CurrentHP = 5000, Level = 50,
                                      MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, IsMonster = true, MonsterId = 494 };
            fight.AddPlayer(sram); fight.AddOpponent(enemy);

            var steal = new SpellEffect { EffectId = 2828, EffectUid = 900301, DiceNum = 100, DiceSide = 100, TargetMask = "A" };
            await FightHandler.HurtAsync(null, fight, sram, 24003, 3, enemy, enemy.CellId, tirada: new[] { steal });

            int taken = 5000 - enemy.CurrentHP;
            Assert.True(taken > 0);
            Assert.Equal(4000 + taken / 2, sram.CurrentHP);
        }
    }
}
