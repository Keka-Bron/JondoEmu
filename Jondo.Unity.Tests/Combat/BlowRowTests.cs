using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A blow is dealt by the blows and leaves no row of its own: Latigo's "damage of the best
    /// element" (2822) left an empty "2822" row on its target's panel, and its "+1 MP" on "i"
    /// is for the caster's summons, not for the foe it whips.
    /// </summary>
    public class BlowRowTests
    {
        private const int Latigo = 31114;

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Latigo_leaves_no_row_of_its_blow_and_no_points_on_the_foe(bool critical)
        {
            var fight = new FightInstance(1, 1);
            var osamodas = new Fighter { Id = 10, TeamId = 0, CellId = 300, MaxHP = 1000, CurrentHP = 1000, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Level = 200, Breed = 2 };
            var foe = new Fighter { Id = 20, TeamId = 1, CellId = 301, MaxHP = 5000, CurrentHP = 5000, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Level = 200, Breed = 1 };
            fight.AddPlayer(osamodas);
            fight.AddOpponent(foe);

            EffectEngine.Resolver(fight, osamodas, Latigo, 3, foe, EffectEngine.AlLanzar, fight.RoundNumber,
                                  celdaApuntada: foe.CellId, critico: critical);

            Assert.DoesNotContain(foe.Buffs.Puestos, b => b.EffectId == 2822);
            Assert.DoesNotContain(foe.Buffs.Puestos, b => b.EffectId == 128);
        }
    }
}
