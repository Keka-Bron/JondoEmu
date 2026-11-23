using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Valley of the Water Lady's monsters strike "A,*e1876" (enemies while the caster is not
    /// under 1876) and "g,*E1876" (allies while it is). Without the state their blows leave
    /// their own side alone.
    /// </summary>
    public class WaterLadyFriendlyFireTests
    {
        [Theory]
        [InlineData(16827)]
        [InlineData(16722)]
        public async Task Without_the_state_a_blow_spares_their_side(int spell)
        {
            if (SpellEffects.De(spell, 1).Count == 0) return;
            var fight = new FightInstance(1, 1);
            var player = new Fighter
            {
                Id = 10, TeamId = 0, CellId = 100, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            var caster = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 272, MaxHP = 3000, CurrentHP = 3000, Level = 130,
                IsMonster = true, MonsterId = 6234, MaxAP = 12, CurrentAP = 12, MaxMP = 3, CurrentMP = 3,
            };
            var ally = new Fighter
            {
                Id = -4, TeamId = 1, CellId = 286, MaxHP = 3000, CurrentHP = 3000, Level = 130,
                IsMonster = true, MonsterId = 6234, MaxAP = 8, CurrentAP = 8, MaxMP = 3, CurrentMP = 3,
            };
            fight.AddPlayer(player);
            fight.AddMonster(caster);
            fight.AddMonster(ally);
            fight.StartFight();

            await FightHandler.AplicarEfectosAsync(null, fight, caster, spell, 1, caster,
                                                   EffectEngine.AlLanzar, ally.CellId);

            Assert.Equal(3000, ally.CurrentHP);
        }
    }
}
