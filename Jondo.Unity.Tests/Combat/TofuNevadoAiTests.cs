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
    /// The Tofu nevado (868) names grades its spells do not have: Caída de Nieve (861, a leap to a
    /// free cell with a blow round where it lands) at grade 6 and Lavado de Cara (878, a blow in a
    /// circle of two round itself) at its own grade, 1 to 5 -- SpellLevels holds grade 1 of each.
    /// A grade with no rows is a spell with no effects: the tactics weighed both as nothing and in
    /// the dungeon review it cast nothing in twelve rounds, next to the character. The monster now
    /// casts them at the nearest grade there is (DatabaseManager.ExistingSpellGrade).
    /// </summary>
    public class TofuNevadoAiTests
    {
        private const int TofuNevado = 868, Snowfall = 861, FaceWash = 878, MonsterCell = 300;

        private static bool HasData => SpellEffects.De(Snowfall, 1).Count > 0 && SpellEffects.De(FaceWash, 1).Count > 0
                                       && DatabaseManager.GetMonsterGradeStats(TofuNevado, 0)?.SpellIds.Count > 0;

        private static (Fighter Tofu, Fighter Enemy, MonsterTactics.Board Board) Board(int grade, int distance)
        {
            var stats = DatabaseManager.GetMonsterGradeStats(TofuNevado, grade)!;
            var tofu = new Fighter
            {
                Id = -1, TeamId = 1, IsMonster = true, MonsterId = TofuNevado, Level = stats.Level, CellId = MonsterCell,
                MaxHP = 500, CurrentHP = 500, MaxAP = 8, CurrentAP = 8, MaxMP = 4, CurrentMP = 4,
                SpellIds = stats.SpellIds, SpellGrades = stats.SpellGrades,
            };
            int cell = Enumerable.Range(0, MapGeometry.MaxCells).First(c => MapGeometry.Distance(MonsterCell, c) == distance);
            var enemy = new Fighter
            {
                Id = 10, TeamId = 0, CellId = cell, Level = 50, MaxHP = 900, CurrentHP = 900,
                MaxAP = 10, CurrentAP = 10, MaxMP = 5, CurrentMP = 5,
            };
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
        [InlineData(0)]
        [InlineData(4)]
        public void Its_spells_are_cast_at_a_grade_that_exists(int grade)
        {
            if (!HasData) return;
            var stats = DatabaseManager.GetMonsterGradeStats(TofuNevado, grade)!;
            Assert.Equal(1, stats.SpellGrades[Snowfall]);
            Assert.Equal(1, stats.SpellGrades[FaceWash]);
        }

        /// <summary>The circle round itself, the enemy beside it: cast on its own cell.</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(4)]
        public void It_washes_the_face_of_the_enemy_beside_it(int grade)
        {
            if (!HasData) return;
            var (tofu, _, board) = Board(grade, 1);
            tofu.Recarga[Snowfall] = 1;

            var next = MonsterTactics.Next(board, tofu, FightHandler.TacticsOf(tofu));

            Assert.NotNull(next);
            Assert.Equal(FaceWash, next!.Spell.Id);
            Assert.Equal(next.From, next.TargetCell);
        }

        /// <summary>The leap: aimed at a free cell in its range, its blow landing on the enemy.</summary>
        [Theory]
        [InlineData(0, 1)]
        [InlineData(4, 3)]
        public void It_leaps_onto_a_free_cell_by_the_enemy(int grade, int distance)
        {
            if (!HasData) return;
            var (tofu, enemy, board) = Board(grade, distance);
            tofu.Recarga[FaceWash] = 1;

            var next = MonsterTactics.Next(board, tofu, FightHandler.TacticsOf(tofu));

            Assert.NotNull(next);
            Assert.Equal(Snowfall, next!.Spell.Id);
            Assert.NotEqual(enemy.CellId, next.TargetCell);
            Assert.NotEqual(next.From, next.TargetCell);
            Assert.InRange(MapGeometry.Distance(next.From, next.TargetCell), 1, 5);
            Assert.Equal(1, MapGeometry.Distance(next.TargetCell, enemy.CellId));
        }
    }
}
