using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A circle-zoned teleport cast at a taken cell -- the Tryde's Prisión Acuosa (6693) and the
    /// Puñometeoro (6772) of Capitán Meno's crew -- takes the caster to the back of the fighter
    /// aimed at. Aimed at his own cell, it was refused every time and the caster never moved.
    /// </summary>
    public class TeleportBehindTests
    {
        private const int FoeCell = 300;

        private static (Fighter caster, Fighter foe) Cast(int spell, int monster, int casterCell)
        {
            var fight = new FightInstance(1, 1);
            var foe = new Fighter { Id = 10, TeamId = 0, CellId = FoeCell, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                                    MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            var caster = new Fighter { Id = -1, TeamId = 1, CellId = casterCell, MaxHP = 6000, CurrentHP = 6000, Level = 200,
                                       IsMonster = true, MonsterId = monster, MaxAP = 8, CurrentAP = 8, MaxMP = 4, CurrentMP = 4 };
            fight.AddPlayer(foe); fight.AddMonster(caster);
            EffectEngine.Resolver(fight, caster, spell, 1, foe, EffectEngine.AlLanzar, fight.RoundNumber,
                                  celdaApuntada: foe.CellId);
            return (caster, foe);
        }

        [Theory]
        [InlineData(6693, 4441)]   // Prisión Acuosa, Tryde
        [InlineData(6772, 4457)]   // Puñometeoro
        public void It_lands_at_the_back_of_the_fighter_aimed_at(int spell, int monster)
        {
            if (!SpellEffects.De(spell, 1).Any(e => e.EffectId == 4)) return;   // no spell data
            int start = Zone.Casillas('Q', 3, FoeCell, FoeCell, 3).First(MapGeometry.IsValid);
            var (caster, foe) = Cast(spell, monster, start);

            Assert.Equal(1, MapGeometry.Distance(caster.CellId, foe.CellId));
            Assert.Equal(MapGeometry.Distance(start, FoeCell) + 1, MapGeometry.Distance(start, caster.CellId));
            Assert.Equal(FoeCell, foe.CellId);
        }
    }
}
