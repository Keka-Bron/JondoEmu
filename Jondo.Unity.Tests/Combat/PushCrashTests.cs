using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// What the client's own fight preview does with a crash that the captures do not show: the
    /// row behind the blocker, a push along a diagonal, a summon's level and the multipliers.
    /// </summary>
    /// <remarks>
    /// Read in the 3.6.10.11 client with Ghidra over the REA IL2CPP seeds (EffectEngine.CollisionOf
    /// names the functions). The formula at rank 0 and the blocker's half stay the captures'
    /// (PushDamageTests, Outcome.CollisionDamageToBlocker).
    /// </remarks>
    public class PushCrashTests
    {
        private static Fighter Fighter(long id, int team, int cell) => new Fighter
        {
            Id = id, TeamId = team, CellId = cell, MaxHP = 1000, CurrentHP = 1000, Level = 100,
            MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3,
        };

        /// <summary>The cell <paramref name="steps"/> steps of (dx, dy) from <paramref name="cell"/>.</summary>
        private static int Along(int cell, int dx, int dy, int steps)
        {
            var (x, y) = MapGeometry.CellToPoint(cell);
            return MapGeometry.PointToCell(x + dx * steps, y + dy * steps);
        }

        private static List<Outcome> Push(FightInstance fight, Fighter caster, Fighter target, int cells)
            => EffectEngine.ResolveEffects(fight, caster, 1, 1, target, EffectEngine.AlLanzar, fight.RoundNumber,
                   new[] { new SpellEffect { EffectId = 5, TargetMask = "A", DiceNum = cells, Triggers = "I" } },
                   aimedCell: target.CellId);

        /// <summary>
        /// Three cells blocked with two fighters lined up behind: the pushed one takes the whole,
        /// the first behind half, the second a quarter (haf.guf, hal.bncs's 2^rank).
        /// </summary>
        [Fact]
        public void The_row_behind_the_blocker_takes_half_at_each_rank()
        {
            var fight = new FightInstance(1, 1);
            int start = 300;
            var caster = Fighter(1, 0, start);
            var target = Fighter(2, 1, Along(start, 1, 0, 1));
            var first = Fighter(3, 1, Along(start, 1, 0, 2));
            var second = Fighter(4, 1, Along(start, 1, 0, 3));
            fight.AddPlayer(caster); fight.AddOpponent(target); fight.AddOpponent(first); fight.AddOpponent(second);

            var crash = Assert.Single(Push(fight, caster, target, 3), o => o.Sobre == target);

            // 3 × (100/2 + 32) / 4 = 61; then 30 and 15.
            Assert.Equal(61, crash.CollisionDamage);
            Assert.Same(first, crash.Blocker);
            Assert.Equal(30, crash.CollisionDamageToBlocker);
            var behind = Assert.Single(crash.FurtherBlockers);
            Assert.Same(second, behind.Who);
            Assert.Equal(15, behind.Damage);
        }

        /// <summary>The row goes no further than the cells left: one blocked cell hurts one blocker.</summary>
        [Fact]
        public void The_row_is_as_long_as_the_cells_left()
        {
            var fight = new FightInstance(1, 1);
            int start = 300;
            var caster = Fighter(1, 0, start);
            var target = Fighter(2, 1, Along(start, 1, 0, 1));
            var first = Fighter(3, 1, Along(start, 1, 0, 2));
            var second = Fighter(4, 1, Along(start, 1, 0, 3));
            fight.AddPlayer(caster); fight.AddOpponent(target); fight.AddOpponent(first); fight.AddOpponent(second);

            var crash = Assert.Single(Push(fight, caster, target, 1), o => o.Sobre == target);

            Assert.Same(first, crash.Blocker);
            Assert.Empty(crash.FurtherBlockers);
        }

        /// <summary>
        /// Along a diagonal -- an even direction of the client's eight, og.ovo -- each blocked cell
        /// counts twice: one cell is 2 × 82 / 4 = 41 where a straight one is 20.
        /// </summary>
        [Fact]
        public void A_diagonal_crash_counts_each_cell_twice()
        {
            var fight = new FightInstance(1, 1);
            int start = 300;
            var caster = Fighter(1, 0, start);
            var target = Fighter(2, 1, Along(start, 1, 1, 1));
            var wall = Fighter(3, 1, Along(start, 1, 1, 2));
            fight.AddPlayer(caster); fight.AddOpponent(target); fight.AddOpponent(wall);

            var crash = Assert.Single(Push(fight, caster, target, 1), o => o.Sobre == target);

            Assert.Equal(41, crash.CollisionDamage);
            Assert.Equal(20, crash.CollisionDamageToBlocker);
        }

        /// <summary>A summon's crash reads its summoner's level (hal.bncs).</summary>
        [Fact]
        public void A_summon_pushes_at_its_summoner_s_level()
        {
            var fight = new FightInstance(1, 1);
            var master = Fighter(1, 0, 300);
            master.Level = 200;
            fight.AddPlayer(master);
            var summon = new Fighter { Id = -5, TeamId = 0, CellId = 301, MaxHP = 50, CurrentHP = 50,
                                       IsMonster = true, Level = 3, Invocador = master.Id };
            fight.Invocar(summon, master);

            Assert.Equal(200, EffectEngine.LevelForPush(fight, summon));
            Assert.Equal(200, EffectEngine.LevelForPush(fight, master));
        }

        /// <summary>
        /// The pusher's 150 and the taker's melee-taken 124 multiply a crash; his resistance does
        /// not, nor the pusher's 107 (ham.irz).
        /// </summary>
        [Fact]
        public void A_crash_goes_through_the_pusher_s_150_and_the_taker_s_taken_percentages()
        {
            var fight = new FightInstance(1, 1);
            var pusher = Fighter(1, 0, 300);
            var taker = Fighter(2, 1, Along(300, 1, 0, 1));
            fight.AddPlayer(pusher); fight.AddOpponent(taker);

            Assert.Equal(100, EffectEngine.PushTaken(fight, pusher, taker, 100, 1));

            pusher.Otras[EffectEngine.TodosLosDanos] = 50;              // 150 at 150 %
            taker.Otras[DamagePercentages.MeleeTaken] = -20;              // 124 at 80 %
            taker.Otras[107] = 100;                                       // not read for a crash
            Assert.Equal(120, EffectEngine.PushTaken(fight, pusher, taker, 100, 1));

            taker.CellId = Along(300, 1, 0, 3);                           // further: 121 instead
            Assert.Equal(150, EffectEngine.PushTaken(fight, pusher, taker, 100, 1));
        }
    }
}
