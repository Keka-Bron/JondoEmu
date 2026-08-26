using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Where a shove ends, and what stopped it.
    /// </summary>
    /// <remarks>
    /// What it stopped against is not cosmetic: hitting another fighter costs both of them health —
    /// the pushed one in full, the wall half — while hitting a wall or the edge only costs the
    /// pushed one. And the count of tiles NOT travelled is the number the whole collision formula
    /// is made of; the old version returned only the destination and threw that away.
    /// </remarks>
    public class ZonePushTests
    {
        /// <summary>Every cell of the grid walkable, so only the arguments decide anything.</summary>
        private static HashSet<int> Everywhere()
            => new HashSet<int>(Enumerable.Range(0, 560).Where(MapGeometry.IsValid));

        /// <summary>A cell with room on both sides, and the one directly behind it.</summary>
        private static (int Caster, int Target, int Behind) ALineOfThree()
        {
            // Picked from the grid rather than written down: take a middle cell, one of its
            // neighbours as the target, and continue in the same direction.
            int caster = 288;
            int target = MapGeometry.GetNeighbors(caster).First();
            int behind = MapGeometry.GetNeighbors(target)
                                    .First(c => c != caster && MapGeometry.Distance(caster, c) == 2);
            return (caster, target, behind);
        }

        [Fact]
        public void An_unobstructed_shove_travels_the_whole_way_and_costs_nothing()
        {
            var (caster, target, _) = ALineOfThree();

            var push = Zone.Push(target, caster, target, 2, Everywhere(), new HashSet<int>());

            Assert.Equal(0, push.BlockedCells);
            Assert.Equal(Zone.PushStop.None, push.Stop);
            Assert.Equal(2, MapGeometry.Distance(target, push.ToCell));
        }

        [Fact]
        public void An_obstacle_stops_it_and_the_untravelled_tiles_are_counted()
        {
            var (caster, target, behind) = ALineOfThree();
            var walkable = Everywhere();
            walkable.Remove(behind);

            var push = Zone.Push(target, caster, target, 3, walkable, new HashSet<int>());

            Assert.Equal(target, push.ToCell);
            Assert.Equal(3, push.BlockedCells);
            Assert.Equal(Zone.PushStop.Obstacle, push.Stop);
        }

        [Fact]
        public void Another_fighter_stops_it_and_is_named()
        {
            // Naming the blocker is what lets the caller give it half the damage. Without it a wall
            // and a piou would be announced the same way and the piou would bleed for nothing.
            var (caster, target, behind) = ALineOfThree();

            var push = Zone.Push(target, caster, target, 3, Everywhere(), new HashSet<int> { behind });

            Assert.Equal(Zone.PushStop.Fighter, push.Stop);
            Assert.Equal(behind, push.BlockerCell);
            Assert.Equal(3, push.BlockedCells);
        }

        [Fact]
        public void A_partly_blocked_shove_counts_only_what_it_could_not_travel()
        {
            var (caster, target, behind) = ALineOfThree();
            int further = MapGeometry.GetNeighbors(behind)
                                     .First(c => MapGeometry.Distance(target, c) == 2);
            var walkable = Everywhere();
            walkable.Remove(further);

            var push = Zone.Push(target, caster, target, 4, walkable, new HashSet<int>());

            Assert.Equal(behind, push.ToCell);
            Assert.Equal(3, push.BlockedCells);
        }

        [Fact]
        public void Zero_tiles_moves_nobody()
        {
            var (caster, target, _) = ALineOfThree();

            var push = Zone.Push(target, caster, target, 0, Everywhere(), new HashSet<int>());

            Assert.Equal(target, push.ToCell);
            Assert.Equal(0, push.BlockedCells);
            Assert.Equal(Zone.PushStop.None, push.Stop);
        }

        [Fact]
        public void A_negative_count_pulls_instead_of_pushing()
        {
            var (caster, target, _) = ALineOfThree();

            var push = Zone.Push(target, caster, target, -1, Everywhere(), new HashSet<int>());

            Assert.Equal(caster, push.ToCell);
        }

        [Fact]
        public void An_invalid_target_stays_where_it_is()
        {
            var push = Zone.Push(288, 288, -1, 2, Everywhere(), new HashSet<int>());

            Assert.Equal(-1, push.ToCell);
            Assert.Equal(0, push.BlockedCells);
        }

        [Fact]
        public void Empujar_still_answers_the_destination_for_its_old_callers()
        {
            // The old signature stays, delegating, so nothing that only wants the destination had
            // to change when the result grew.
            var (caster, target, _) = ALineOfThree();

            int destination = Zone.Empujar(target, caster, target, 2, Everywhere(), new HashSet<int>());

            Assert.Equal(Zone.Push(target, caster, target, 2, Everywhere(), new HashSet<int>()).ToCell,
                         destination);
        }
    }
}
