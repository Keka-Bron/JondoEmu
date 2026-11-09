using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.World.Combat;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// One carried who dies is set down for good: his carrier carries nobody, and neither shows
    /// the carry's states any more. Left "carrying" a dead bamboo, the Pandawa was listed to the
    /// client as carrying an entity no longer on the board.
    /// </summary>
    public class CarriedDeathTests
    {
        [Fact]
        public async Task A_carried_one_struck_down_frees_his_carrier()
        {
            var fight = new FightInstance(1, 1);
            var pandawa = new Fighter { Id = 1, TeamId = 0, CellId = 300, MaxHP = 2000, CurrentHP = 2000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            var foe = new Fighter { Id = -1, TeamId = 1, CellId = 300, MaxHP = 50, CurrentHP = 50, Level = 50, MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, IsMonster = true, MonsterId = 494 };
            var hitter = new Fighter { Id = 2, TeamId = 0, CellId = 330, MaxHP = 2000, CurrentHP = 2000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            fight.AddPlayer(pandawa); fight.AddPlayer(hitter); fight.AddOpponent(foe);
            pandawa.Carrying = foe.Id;
            foe.CarriedBy = pandawa.Id;
            pandawa.Buffs.PonerEstado(EffectSupport.CarryingState);
            foe.Buffs.PonerEstado(EffectSupport.CarriedState);

            // Struck down where he is carried: a blow aimed at the cell finds his carrier.
            await FightHandler.TestKillAsync(null, fight, foe, hitter);

            Assert.True(!foe.IsAlive, $"life {foe.CurrentHP}");
            Assert.Equal(0, pandawa.Carrying);
            Assert.Equal(0, foe.CarriedBy);
            Assert.True(!pandawa.Buffs.TieneEstado(EffectSupport.CarryingState), "still in the carrying state");
        }
    }
}
