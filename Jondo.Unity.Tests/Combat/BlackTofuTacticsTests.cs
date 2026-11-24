using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The black Tofu (796, and its Tofu House copy 5223) and its Fogosidad Tofuesca (8710): a blow
    /// one cell away that pushes, steals MP and heals the Tofu itself. That caster-only heal was
    /// weighed as a heal aimed at its own side, which cancelled the blow's utility, so the spell was
    /// worth nothing and never cast.
    /// </summary>
    public sealed class BlackTofuTacticsTests
    {
        private const int Fogosidad = 8710, Other = 8709;

        private static (Fighter Tofu, Fighter Enemy, MonsterTactics.Board Board) Duel(int monster, int grade)
        {
            var stats = DatabaseManager.GetMonsterGradeStats(monster, grade)!;
            var tofu = new Fighter
            {
                Id = -1, TeamId = 1, IsMonster = true, MonsterId = monster, Level = stats.Level, CellId = 300,
                MaxHP = 500, CurrentHP = 300, MaxAP = 6, CurrentAP = 6, MaxMP = 4, CurrentMP = 4,
                SpellIds = stats.SpellIds, SpellGrades = stats.SpellGrades,
            };
            int beside = Enumerable.Range(0, MapGeometry.MaxCells).First(c => MapGeometry.Distance(300, c) == 1);
            var enemy = new Fighter { Id = 10, TeamId = 0, CellId = beside, Level = 50, MaxHP = 900, CurrentHP = 900,
                                      MaxAP = 10, CurrentAP = 10, MaxMP = 5, CurrentMP = 5 };
            var fight = new FightInstance(1, 1);
            fight.AddPlayer(enemy);
            fight.AddMonster(tofu);
            var board = new MonsterTactics.Board
            {
                Fighters = new[] { tofu, enemy },
                Reach = (row, caster, from, aim) => EffectEngine.ReachOf(fight, caster, row, from, aim),
            };
            return (tofu, enemy, board);
        }

        [Theory]
        [InlineData(796, 0)]
        [InlineData(5223, 2)]
        public void The_self_heal_of_fogosidad_tofuesca_is_not_weighed_as_a_heal_for_its_side(int monster, int grade)
        {
            var (tofu, _, _) = Duel(monster, grade);
            var spell = FightHandler.TacticsOf(tofu).First(s => s.Id == Fogosidad);
            Assert.Equal(0.0, spell.Heal);
            Assert.True(spell.Utility > 0);
        }

        [Theory]
        [InlineData(796, 0)]
        [InlineData(5223, 2)]
        public void The_black_tofu_casts_fogosidad_tofuesca_on_an_enemy_beside_it(int monster, int grade)
        {
            var (tofu, enemy, board) = Duel(monster, grade);
            var options = MonsterTactics.Options(board, tofu, FightHandler.TacticsOf(tofu)).ToList();
            Assert.Contains(options, o => o.Spell.Id == Fogosidad && o.TargetCell == enemy.CellId && o.Score > 0);

            tofu.Recarga[Other] = 1;
            var next = MonsterTactics.Next(board, tofu, FightHandler.TacticsOf(tofu));
            Assert.NotNull(next);
            Assert.Equal(Fogosidad, next!.Spell.Id);
        }
    }
}
