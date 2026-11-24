using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A monster whose spell lays a trap: the tactics never aim it at a cell that already holds
    /// one (the engine refuses that cast without charging it), and a refused cast is not retried.
    /// </summary>
    public class TrapRecastTests
    {
        private const int Centre = 300;

        private static Fighter Monster()
            => new Fighter { Id = -1, TeamId = 1, CellId = Centre, MaxHP = 1000, CurrentHP = 1000, CurrentAP = 6, CurrentMP = 0, Level = 100, IsMonster = true };

        private static Fighter Player(int cell)
            => new Fighter { Id = 1, TeamId = 0, CellId = cell, MaxHP = 1000, CurrentHP = 1000, CurrentAP = 6, CurrentMP = 3, Level = 200 };

        /// <summary>A melee spell with a trap row, as a monster's summary: a free cell beside the enemy.</summary>
        private static MonsterTactics.Spell Pino(bool placesTrap) => new MonsterTactics.Spell
        {
            Id = 8649, Grade = 1, Cost = 3, MinRange = 1, MaxRange = 1, PerTurn = 1, NeedsFreeCell = true,
            Utility = 25, UtilityOnEnemies = true, UtilityEnemyMasks = new[] { "A" }, PlacesTrap = placesTrap,
        };

        private static int Neighbour(int cell, int skip = 0) => MapGeometry.GetNeighbors(cell).Skip(skip).First();

        [Fact]
        public void Cells_one_step_apart_in_the_tilted_grid_are_in_melee_reach()
        {
            // 247 and 234: x 18/y 1 and x 18/y 2, one step, though their ids differ by 13.
            Assert.Equal(1, MapGeometry.Distance(247, 234));
        }

        [Fact]
        public void It_never_aims_a_trap_at_a_cell_that_holds_one()
        {
            var monster = Monster();
            var player = Player(Neighbour(Neighbour(Centre)));
            var trapped = new HashSet<int>(MapGeometry.GetNeighbors(player.CellId));
            var board = new MonsterTactics.Board { Fighters = new[] { monster, player }, HoldsATrap = trapped.Contains };

            var options = MonsterTactics.Options(board, monster, new[] { Pino(true) });

            Assert.DoesNotContain(options, o => trapped.Contains(o.TargetCell));
        }

        [Fact]
        public void Without_the_trap_flag_the_same_cells_are_still_aimed()
        {
            // The check is for trap-laying spells only: the engine's rule is not extended to the rest.
            var monster = Monster();
            var player = Player(Neighbour(Neighbour(Centre)));
            var trapped = new HashSet<int>(MapGeometry.GetNeighbors(player.CellId));
            var board = new MonsterTactics.Board { Fighters = new[] { monster, player }, HoldsATrap = trapped.Contains };

            var options = MonsterTactics.Options(board, monster, new[] { Pino(false) });

            Assert.Contains(options, o => trapped.Contains(o.TargetCell));
        }

        [Fact]
        public void A_free_cell_is_still_aimed()
        {
            var monster = Monster();
            var player = Player(Neighbour(Neighbour(Centre)));
            var board = new MonsterTactics.Board { Fighters = new[] { monster, player } };

            Assert.NotEmpty(MonsterTactics.Options(board, monster, new[] { Pino(true) }));
        }

        [Fact]
        public void The_engine_reads_laying_a_trap_from_the_spell_rows()
        {
            Assert.True(Jondo.Unity.Server.Handlers.FightHandler.LaysATrap(8649, 1));
            Assert.False(Jondo.Unity.Server.Handlers.FightHandler.LaysATrap(1, 1));
        }
    }
}
