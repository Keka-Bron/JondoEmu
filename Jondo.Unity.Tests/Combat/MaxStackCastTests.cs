using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A spell cast again and again on one target keeps no more of its rows than its grade's
    /// maxStack: Tifón's -100 agility (maxStack 2), Lanzamiento de Pala's -4 range (maxStack 1).
    /// </summary>
    public class MaxStackCastTests
    {
        private static (FightInstance Fight, Fighter Caster, Fighter Foe) Board()
        {
            var fight = new FightInstance(1, 1);
            var caster = new Fighter { Id = 10, TeamId = 0, CellId = 300, MaxHP = 1000, CurrentHP = 1000, MaxAP = 12, CurrentAP = 12, Level = 200 };
            var foe = new Fighter { Id = -50, TeamId = 1, CellId = 302, MaxHP = 50000, CurrentHP = 50000, IsMonster = true, MonsterId = 494 };
            fight.AddPlayer(caster);
            fight.AddMonster(foe);
            return (fight, caster, foe);
        }

        [Theory]
        [InlineData(12976, 3, 154, 2)]
        [InlineData(13330, 1, 116, 1)]
        public void Cast_three_times_it_keeps_its_maxstack(int spell, int grade, int effect, int maxStack)
        {
            var (fight, caster, foe) = Board();
            for (int cast = 0; cast < 3; cast++)
                EffectEngine.Resolver(fight, caster, spell, grade, foe, EffectEngine.AlLanzar, fight.RoundNumber, celdaApuntada: foe.CellId);

            Assert.Equal(maxStack, foe.Buffs.Puestos.Count(r => r.EffectId == effect && r.HechizoOrigen == spell));
        }

        [Theory]
        [InlineData(12976, 3, 154, 2)]
        [InlineData(13330, 1, 116, 1)]
        public void Critical_or_not_it_keeps_its_maxstack(int spell, int grade, int effect, int maxStack)
        {
            var (fight, caster, foe) = Board();
            for (int cast = 0; cast < 3; cast++)
                EffectEngine.Resolver(fight, caster, spell, grade, foe, EffectEngine.AlLanzar, fight.RoundNumber,
                                      celdaApuntada: foe.CellId, critico: cast % 2 == 0);

            Assert.Equal(maxStack, foe.Buffs.Puestos.Count(r => r.EffectId == effect && r.HechizoOrigen == spell));
        }
    }
}
