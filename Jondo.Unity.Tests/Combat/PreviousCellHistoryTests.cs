using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Back to the previous position (1100) walks a history back, one cell at a time, as
    /// «xelor-recuerdo» measures on the Cómplice: every cell walked is one more back, a return
    /// takes its cell off without noting the one it leaves, and the history outlives the turn.
    /// </summary>
    [Collection("MapManager")]
    public class PreviousCellHistoryTests
    {
        private const int Recuerdo = 13281;
        private const int Congelacion = 13245;

        [Fact]
        public void A_walk_notes_every_cell_and_each_return_takes_one_off()
        {
            var complice = new Fighter { Id = -4, MaxHP = 100, CurrentHP = 100, CellId = 327 };

            complice.Andar(new[] { 327, 314, 300, 287 });
            Assert.Equal(287, complice.CellId);
            Assert.Equal(300, complice.CasillaAnterior);

            complice.VolverALaAnterior(complice.CasillaAnterior);
            Assert.Equal(300, complice.CellId);
            complice.VolverALaAnterior(complice.CasillaAnterior);
            Assert.Equal(314, complice.CellId);
            Assert.Equal(327, complice.CasillaAnterior);

            complice.VolverALaAnterior(complice.CasillaAnterior);
            Assert.Equal(327, complice.CellId);
            Assert.Equal(-1, complice.CasillaAnterior);
        }

        [Fact]
        public void A_plan_puts_the_whole_history_back()
        {
            var who = new Fighter { Id = 1, MaxHP = 100, CurrentHP = 100, CellId = 200 };
            who.MoverA(215);
            who.MoverA(230);
            var kept = who.CellHistory();

            who.VolverALaAnterior(215);
            who.MoverA(400);
            who.RestoreCellHistory(kept);

            Assert.Equal(new[] { 200, 215 }, who.CellHistory());
        }

        [Fact]
        public void Recuerdo_and_Congelacion_go_back_along_the_captured_walks()
        {
            if (!SpellTable.IsLoaded) return;
            var fight = new FightInstance(9_970_001, 1, 1);
            var xelor = new Fighter { Id = 1, TeamId = 0, CellId = 354, MaxHP = 6000, CurrentHP = 6000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 5 };
            var complice = new Fighter { Id = -4, TeamId = 0, CellId = 327, MaxHP = 600, CurrentHP = 600, Level = 1, IsMonster = true, Invocador = 1 };
            fight.AddPlayer(xelor);
            fight.AddPlayer(complice);

            int Cast(int spell)
            {
                int grade = SpellTable.GradeFor(spell, 200);
                EffectEngine.Resolver(fight, xelor, spell, grade, complice, EffectEngine.AlLanzar, 1, celdaApuntada: complice.CellId);
                fight.Telefrags.Clear();
                return complice.CellId;
            }

            // Its first turn: 327 → 314 → 300 → 287, then two Recuerdo.
            complice.Andar(new[] { 327, 314, 300, 287 });
            Assert.Equal(300, Cast(Recuerdo));
            Assert.Equal(314, Cast(Recuerdo));

            // Its second: 314 → 300 → 287, then Recuerdo, Recuerdo, Congelación and Congelación.
            complice.Andar(new[] { 314, 300, 287 });
            Assert.Equal(300, Cast(Recuerdo));
            Assert.Equal(314, Cast(Recuerdo));
            Assert.Equal(327, Cast(Congelacion));
            Assert.Equal(327, Cast(Congelacion));
        }
    }
}
