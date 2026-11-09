using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Sacrogito's spells "sacrifican una parte de la vida del lanzador": a share of what he
    /// has left. Berserker's 70% on his maximum killed him whenever he stood under 70% of it.
    /// </summary>
    public class SelfSacrificeTests
    {
        private const int Berserker = 12743;

        [Fact]
        public async Task Berserker_under_70_percent_does_not_kill_the_Sacrogito()
        {
            var fight = new FightInstance(1, 1);
            var sacrogito = new Fighter { Id = 1, TeamId = 0, CellId = 300, MaxHP = 4000, CurrentHP = 1500, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Level = 200, Breed = 11 };
            fight.AddPlayer(sacrogito);
            fight.AddOpponent(new Fighter { Id = -1, TeamId = 1, CellId = 200, MaxHP = 900, CurrentHP = 900, IsMonster = true, MonsterId = 494 });

            await FightHandler.AplicarEfectosAsync(null, fight, sacrogito, Berserker, 2, sacrogito, EffectEngine.AlLanzar, sacrogito.CellId);

            Assert.True(sacrogito.IsAlive);
            Assert.Equal(450, sacrogito.CurrentHP);
        }
    }
}
