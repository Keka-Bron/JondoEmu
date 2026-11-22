using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Missiz Frizz's passive (3517) has her cast Glace de pic (3518) on herself when she is hit:
    /// a pull of 6 on a star of 7 around her. «Elle attire à son contact tous les personnages en
    /// ligne et en diagonale» (Dofus pour les noobs). A diagonal pull goes half its cells, so one
    /// standing one diagonal step away is already as near as it can bring him.
    /// </summary>
    public class FrozenPeakPullTests
    {
        private const int Frizz = 3391, GlaceDePic = 3518;
        private const int BossCell = 215;

        private static Fighter PullFrom(int playerCell)
        {
            var fight = new FightInstance(1, 1);
            var boss = new Fighter
            {
                Id = -1, TeamId = 1, CellId = BossCell, IsMonster = true, MonsterId = Frizz, Level = 200,
                MaxHP = 20000, CurrentHP = 20000, MaxAP = 12, CurrentAP = 12, MaxMP = 4, CurrentMP = 4,
            };
            var me = new Fighter
            {
                Id = 10, TeamId = 0, CellId = playerCell, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            fight.AddMonster(boss); fight.AddPlayer(me);
            EffectEngine.Resolver(fight, boss, GlaceDePic, 1, boss, EffectEngine.AlLanzar,
                                  fight.RoundNumber, celdaApuntada: boss.CellId);
            return me;
        }

        [Fact]
        public void One_in_line_is_pulled_into_contact()
        {
            if (SpellEffects.De(GlaceDePic, 1).Count == 0) return;   // no spell data
            int inLine = Zone.Casillas('Q', 4, BossCell, BossCell, 4).First(MapGeometry.IsValid);
            Assert.Equal(1, MapGeometry.Distance(PullFrom(inLine).CellId, BossCell));
        }

        [Fact]
        public void One_four_cells_down_a_diagonal_is_pulled_to_the_next_diagonal_cell()
        {
            if (SpellEffects.De(GlaceDePic, 1).Count == 0) return;
            int diagonal = BossCell - 56;   // two diagonal steps straight up the screen
            Assert.Equal(2, MapGeometry.Distance(PullFrom(diagonal).CellId, BossCell));
        }

        [Fact]
        public void One_a_single_diagonal_step_away_stays_put()
        {
            if (SpellEffects.De(GlaceDePic, 1).Count == 0) return;
            Assert.Equal(BossCell - 28, PullFrom(BossCell - 28).CellId);
        }
    }
}
