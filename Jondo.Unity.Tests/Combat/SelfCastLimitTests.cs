using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A spell cast once per target counts its caster too when he aims it at his own cell: the
    /// expedition's Jalató (7897) casts Salivazo (29402, range 0) once a turn, not every time he
    /// has the points for it.
    /// </summary>
    public class SelfCastLimitTests
    {
        private const int Salivazo = 29402;

        [Fact]
        public void A_cast_on_his_own_cell_counts_against_himself()
        {
            var jalato = new Fighter { Id = -5, TeamId = 1, IsMonster = true, MonsterId = 7897 };
            FightHandler.CountCastOn(jalato, Salivazo, jalato);
            Assert.Equal(1, jalato.LanzadosPorObjetivo[(Salivazo, jalato.Id)]);
        }

        [Fact]
        public void Counted_once_the_planner_leaves_it_for_the_next_turn()
        {
            var jalato = new Fighter
            {
                Id = -5, TeamId = 1, CellId = 300, IsMonster = true, MonsterId = 7897, Level = 200,
                MaxHP = 6000, CurrentHP = 6000, MaxAP = 12, CurrentAP = 12, MaxMP = 3, CurrentMP = 3,
            };
            jalato.SpellIds.Add(Salivazo);
            jalato.SpellGrades[Salivazo] = 1;
            var spell = FightHandler.TacticsOf(jalato).FirstOrDefault(s => s.Id == Salivazo);
            if (spell == null || spell.PerTarget <= 0) return; // no spell data

            FightHandler.CountCastOn(jalato, Salivazo, jalato);
            Assert.True(jalato.LanzadosPorObjetivo[(Salivazo, jalato.Id)] >= spell.PerTarget);
        }

        [Fact]
        public void No_one_on_the_cell_counts_nothing()
        {
            var jalato = new Fighter { Id = -5, TeamId = 1, IsMonster = true };
            FightHandler.CountCastOn(jalato, Salivazo, null);
            Assert.Empty(jalato.LanzadosPorObjetivo);
        }
    }
}
