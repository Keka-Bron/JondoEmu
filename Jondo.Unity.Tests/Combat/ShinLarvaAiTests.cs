using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Shin Larva (457) and its Hundimiento (506): range 0, cast on its own cell, -6 MP (1080) to
    /// every enemy in a circle of 63 -- the whole map --, AP to all and a state on the enemies.
    /// The guide has it from the second turn and then every three. Weighed on the boss himself,
    /// the removal was worth nothing and he only ever cast his summon (507) and his Deglución
    /// (510): the removal of a spell cast on the caster is weighed on the enemies its zone
    /// reaches (Spell.RemovalZoneRow).
    /// </summary>
    public class ShinLarvaAiTests
    {
        private const int ShinLarva = 457, Sinking = 506, Summon = 507, Gulp = 510;
        private const int Grade = 3, BossCell = 300;

        private static bool HasData => SpellEffects.De(Sinking, Grade).Count > 0;

        private static (Fighter Boss, MonsterTactics.Board Board, List<MonsterTactics.Spell> Spells) Board(int distance, bool invulnerable)
        {
            var boss = new Fighter
            {
                Id = -1, TeamId = 1, IsMonster = true, MonsterId = ShinLarva, Level = 60, CellId = BossCell,
                MaxHP = 3000, CurrentHP = 3000, MaxAP = 9, CurrentAP = 9, MaxMP = 2, CurrentMP = 2,
                SpellIds = new List<int> { Sinking, Summon, Gulp },
                SpellGrades = new Dictionary<int, int> { [Sinking] = Grade, [Summon] = 1, [Gulp] = Grade },
            };
            int cell = Enumerable.Range(0, MapGeometry.MaxCells).First(c => MapGeometry.Distance(BossCell, c) == distance);
            var enemy = new Fighter
            {
                Id = 10, TeamId = 0, CellId = cell, Level = 60, MaxHP = 3000, CurrentHP = 3000,
                MaxAP = 10, CurrentAP = 7, MaxMP = 5, CurrentMP = 5,
            };
            // The harness keeps itself alive with an admin invulnerability: the AP and MP still go.
            if (invulnerable) enemy.Buffs.PonerEstado(56);
            var fight = new FightInstance(1, 1);
            fight.AddPlayer(enemy);
            fight.AddMonster(boss);
            var board = new MonsterTactics.Board
            {
                Fighters = new[] { boss, enemy },
                Reach = (row, caster, from, aim) => EffectEngine.ReachOf(fight, caster, row, from, aim),
            };
            return (boss, board, FightHandler.TacticsOf(boss));
        }

        [Theory]
        [InlineData(1, false)]
        [InlineData(5, false)]
        [InlineData(1, true)]
        [InlineData(8, true)]
        public void It_sinks_the_enemies_from_its_own_cell(int distance, bool invulnerable)
        {
            if (!HasData) return;
            var (boss, board, spells) = Board(distance, invulnerable);

            var next = MonsterTactics.Next(board, boss, spells);

            Assert.NotNull(next);
            Assert.Equal(Sinking, next!.Spell.Id);
            Assert.Equal(BossCell, next.TargetCell);
        }

        /// <summary>Born waiting one round, and three between casts: the AI keeps to them.</summary>
        [Fact]
        public void It_waits_its_first_round_and_its_interval()
        {
            if (!HasData) return;
            var (boss, board, spells) = Board(2, false);
            Assert.Equal(1, FightHandler.BornWaiting(Sinking, Grade));
            Assert.Equal(3, spells.Single(s => s.Id == Sinking).Interval);

            boss.Recarga[Sinking] = FightHandler.BornWaiting(Sinking, Grade);
            Assert.DoesNotContain(MonsterTactics.Options(board, boss, spells), o => o.Spell.Id == Sinking);

            boss.Recarga[Sinking] = 0;
            Assert.Contains(MonsterTactics.Options(board, boss, spells), o => o.Spell.Id == Sinking);
        }
    }
}
