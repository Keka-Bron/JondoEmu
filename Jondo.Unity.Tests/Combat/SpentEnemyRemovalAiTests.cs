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
    /// Self-cast AP and MP removals were weighed by what the enemy had LEFT, and during a monster's
    /// turn a player who has spent his turn has 0 and 0 left. The removal lands on his next turn,
    /// when he holds his full points again, so it was worth nothing exactly when it mattered: the
    /// dungeon review saw boss 2975 never cast 2444 (a push and -2 AP round itself) and its summon
    /// 2976 never cast 2448 (-100 MP round itself, its only spell), so the summon stood idle.
    /// What a removal is worth now asks his full points (MonsterTactics.HoldsPoints).
    /// </summary>
    public class SpentEnemyRemovalAiTests
    {
        private const int Boss = 2975, BossRemoval = 2444, Summon = 2976, SummonRemoval = 2448, MonsterCell = 300;

        private static bool HasData => SpellEffects.De(BossRemoval, 1).Count > 0 && SpellEffects.De(SummonRemoval, 1).Count > 0
                                       && DatabaseManager.GetMonsterGradeStats(Boss, 0)?.SpellIds.Contains(BossRemoval) == true
                                       && Summons.De(Summon, 1) != null;

        /// <summary>An enemy beside the monster who has spent every point of his turn.</summary>
        private static (Fighter Enemy, MonsterTactics.Board Board) Board(Fighter monster)
        {
            int cell = Enumerable.Range(0, MapGeometry.MaxCells).First(c => MapGeometry.Distance(MonsterCell, c) == 1);
            var enemy = new Fighter
            {
                Id = 10, TeamId = 0, CellId = cell, Level = 200, MaxHP = 3000, CurrentHP = 3000,
                MaxAP = 12, CurrentAP = 0, MaxMP = 6, CurrentMP = 0,
            };
            var fight = new FightInstance(1, 1);
            fight.AddPlayer(enemy);
            fight.AddMonster(monster);
            var board = new MonsterTactics.Board
            {
                Fighters = new[] { monster, enemy },
                Reach = (row, caster, from, aim) => EffectEngine.ReachOf(fight, caster, row, from, aim),
            };
            return (enemy, board);
        }

        [Fact]
        public void The_boss_casts_its_removal_at_an_enemy_who_has_spent_his_turn()
        {
            if (!HasData) return;
            var stats = DatabaseManager.GetMonsterGradeStats(Boss, 0)!;
            var boss = new Fighter
            {
                Id = -1, TeamId = 1, IsMonster = true, MonsterId = Boss, Level = stats.Level, CellId = MonsterCell,
                MaxHP = 1200, CurrentHP = 1200, MaxAP = stats.ActionPoints, CurrentAP = stats.ActionPoints, MaxMP = 4, CurrentMP = 4,
                SpellIds = stats.SpellIds, SpellGrades = stats.SpellGrades,
            };
            var (_, board) = Board(boss);

            var options = MonsterTactics.Options(board, boss, FightHandler.TacticsOf(boss));
            Assert.Contains(options, o => o.Spell.Id == BossRemoval && o.Score > 0);
        }

        [Fact]
        public void The_summon_casts_its_only_spell_at_an_enemy_who_has_spent_his_turn()
        {
            if (!HasData) return;
            var recipe = Summons.De(Summon, 1)!;
            Assert.Contains(recipe.Hechizos, h => h.Spell == SummonRemoval);
            var summon = new Fighter
            {
                Id = -2, TeamId = 1, IsMonster = true, MonsterId = Summon, Level = 50, CellId = MonsterCell,
                MaxHP = 300, CurrentHP = 300, MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3,
                HechizosDeInvocado = recipe.Hechizos,
            };
            var (_, board) = Board(summon);

            var next = MonsterTactics.Next(board, summon, FightHandler.TacticsOf(summon));
            Assert.NotNull(next);
            Assert.Equal(SummonRemoval, next!.Spell.Id);
        }

        [Fact]
        public void Spent_points_still_count_and_no_points_at_all_do_not()
        {
            Assert.True(MonsterTactics.HoldsPoints(new Fighter { MaxAP = 12, CurrentAP = 0, MaxMP = 6, CurrentMP = 0 }));
            Assert.False(MonsterTactics.HoldsPoints(new Fighter { MaxAP = 0, CurrentAP = 0, MaxMP = 0, CurrentMP = 0 }));
        }
    }

    /// <summary>
    /// A summoned monster's spells skipped the grade fallback that a placed monster's get
    /// (DatabaseManager.ExistingSpellGrade): 18 summon templates name 34 grades their spells do not
    /// have, and a grade with no rows is a spell that does nothing. Template 2762 names spell 2006
    /// at grade 6, and SpellLevels holds 2006 at grades 1 to 3 only.
    /// </summary>
    public class SummonSpellGradeTests
    {
        private const int Template = 2762, Spell = 2006;

        [Fact]
        public void A_summon_casts_its_spells_at_a_grade_that_exists()
        {
            if (SpellEffects.De(Spell, 1).Count == 0) return;
            bool sawIt = false;
            foreach (int grade in Enumerable.Range(1, 6))
            {
                var recipe = Summons.De(Template, grade);
                if (recipe == null) return;
                foreach (var (spell, spellGrade) in recipe.Hechizos.Where(h => h.Spell == Spell))
                {
                    sawIt = true;
                    Assert.True(SpellEffects.De(spell, spellGrade).Count > 0, $"grade {grade}: spell {spell} at grade {spellGrade} has no rows");
                }
            }
            Assert.True(sawIt);
        }
    }
}
