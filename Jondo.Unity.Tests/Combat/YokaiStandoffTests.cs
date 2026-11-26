using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The library yokais of «En la encrucijada de los mundos» (2150) hit from ten cells, but keep
    /// the two cells of their Yoku (content/monsters/standoffs.json): the fight is won by reaching
    /// all five in one turn, and at ten cells, out of sight, none could be reached.
    /// </summary>
    [Collection("MapManager")]
    public class YokaiStandoffTests
    {
        private const int YokaiRuidoso = 6237;
        private static readonly int[] Kit = { 16975, 16976, 16977, 16980 };

        [Fact]
        public void A_yokai_ends_its_turn_two_cells_away()
        {
            if (MonsterStandoffs.For(YokaiRuidoso) != 2) return;   // no content
            var fight = new FightInstance(9_999_778, 1, 1);
            var me = new Fighter
            {
                Id = 8_990_000_003, TeamId = 0, CellId = 300, MaxHP = 4500, CurrentHP = 4500, Level = 200,
                CurrentAP = 8, MaxAP = 8, CurrentMP = 3, MaxMP = 3,
            };
            int six = Enumerable.Range(0, MapGeometry.MaxCells)
                                .First(c => MapGeometry.Distance(300, c) == 6
                                            && MapGeometry.CellToPoint(c).Item2 == MapGeometry.CellToPoint(300).Item2);
            var yokai = new Fighter
            {
                Id = -2, TeamId = 1, IsMonster = true, MonsterId = YokaiRuidoso, CellId = six, MaxHP = 3600,
                CurrentHP = 3600, Level = 100, CurrentAP = 8, MaxAP = 8, CurrentMP = 10, MaxMP = 10,
            };
            yokai.SpellIds.AddRange(Kit);
            fight.AddPlayer(me); fight.AddMonster(yokai);
            fight.StartFight();
            var board = new MonsterTactics.Board
            {
                Fighters = new[] { yokai, me },
                Reach = (row, caster, from, aim) => EffectEngine.ReachOf(fight, caster, row, from, aim),
                CanSummon = (c, t, g) => FightHandler.FitsTheSummonLimit(fight, c, t, g),
            };
            var spells = FightHandler.TacticsOf(yokai);
            if (spells.Count < Kit.Length) return;   // no spell data

            var place = MonsterTactics.Reposition(board, yokai, spells);
            Assert.Equal(2, MapGeometry.Distance(place[^1], me.CellId));
        }
    }
}
