using System.Linq;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The isometric grid: neighbours, distance, and the rule that there are no diagonals.
    /// </summary>
    /// <remarks>
    /// Everything about movement and spell range sits on top of this. The grid was mirrored once
    /// with respect to the client's — it flipped the y axis on even rows and shifted x on odd ones
    /// — and the symptom was a piou walking through another piou: the code did check whether a cell
    /// was taken, it just checked the wrong cell.
    /// </remarks>
    public class MapGeometryTests
    {
        private const int SomewhereInTheMiddle = 288;

        [Fact]
        public void A_cell_in_the_middle_has_exactly_four_neighbours()
        {
            Assert.Equal(4, MapGeometry.GetNeighbors(SomewhereInTheMiddle).Count());
        }

        [Fact]
        public void Every_neighbour_is_one_step_away_and_never_diagonal()
        {
            // In Dofus you do not walk diagonally: from a cell you reach only the four that touch
            // it, which in rhombus coordinates are the ones at distance one. The monster pathfinder
            // expands through exactly this list, so a diagonal here becomes a monster teleporting.
            foreach (int neighbour in MapGeometry.GetNeighbors(SomewhereInTheMiddle))
            {
                Assert.Equal(1, MapGeometry.Distance(SomewhereInTheMiddle, neighbour));
            }
        }

        [Fact]
        public void Being_a_neighbour_goes_both_ways()
        {
            foreach (int neighbour in MapGeometry.GetNeighbors(SomewhereInTheMiddle))
            {
                Assert.Contains(SomewhereInTheMiddle, MapGeometry.GetNeighbors(neighbour));
            }
        }

        [Fact]
        public void A_cell_is_at_no_distance_from_itself()
        {
            // Spells of range 0-0 are cast on the caster's own cell, and there are 1,555 of them in
            // the monster arsenal. If this were not zero, none of them could ever be cast.
            Assert.Equal(0, MapGeometry.Distance(SomewhereInTheMiddle, SomewhereInTheMiddle));
        }

        [Fact]
        public void Distance_is_symmetric()
        {
            Assert.Equal(MapGeometry.Distance(100, 400), MapGeometry.Distance(400, 100));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        [InlineData(int.MaxValue)]
        public void An_invalid_cell_has_no_neighbours_rather_than_throwing(int cell)
        {
            // Cell numbers arrive from the client, so they can be anything at all.
            Assert.False(MapGeometry.IsValid(cell));
            Assert.Empty(MapGeometry.GetNeighbors(cell));
        }

        [Fact]
        public void The_distance_to_an_invalid_cell_is_absurdly_large_rather_than_an_exception()
        {
            // Whatever it is, it must be far enough that no spell range ever includes it.
            Assert.True(MapGeometry.Distance(SomewhereInTheMiddle, -1) > 100);
        }

        [Fact]
        public void A_cell_on_the_edge_has_fewer_neighbours_and_still_works()
        {
            // Cell zero is a corner. Everything that walks has to cope with the short list.
            Assert.True(MapGeometry.GetNeighbors(0).Count() < 4);
        }

        /// <summary>
        /// A perfect diagonal passes between the cells at its corners, and they never close it,
        /// even with an obstacle on both sides: a Steamer at 275 was refused 303 between a crate
        /// (289) and a Feu Follet (290), which the client offered. A cell ON the diagonal still does.
        /// </summary>
        [Fact]
        public void A_diagonal_passes_between_two_obstacles_at_its_corner()
        {
            Assert.Equal((1, -1), (MapGeometry.CellToPoint(303).X - MapGeometry.CellToPoint(275).X,
                                   MapGeometry.CellToPoint(303).Y - MapGeometry.CellToPoint(275).Y));
            Assert.True(MapGeometry.HasLineOfSight(275, 303, cell => cell == 289 || cell == 290));

            // Two diagonal steps: the cell in the middle of the diagonal blocks it.
            var (x, y) = MapGeometry.CellToPoint(275);
            int middle = MapGeometry.PointToCell(x + 1, y - 1);
            int far = MapGeometry.PointToCell(x + 2, y - 2);
            Assert.False(MapGeometry.HasLineOfSight(275, far, cell => cell == middle));
            Assert.True(MapGeometry.HasLineOfSight(275, far, cell => cell != middle && cell != 275 && cell != far));
        }

        /// <summary>
        /// The sight line is the client's, cell for cell. From 235 to 246 it crosses 248, 234,
        /// 247 and 233, so a fighter on any of them hides 246, as the client shows it; the old
        /// walk kept sight whenever one cell beside the line was open, and a monster struck
        /// through the fighter in front of its target.
        /// </summary>
        [Fact]
        public void The_sight_line_is_the_clients()
        {
            Assert.Equal(new[] { 248, 234, 247, 233, 246 }, MapGeometry.SightLine(235, 246));
            Assert.False(MapGeometry.HasLineOfSight(235, 246, cell => cell == 233));
            Assert.False(MapGeometry.HasLineOfSight(235, 246, cell => cell == 248));
            Assert.True(MapGeometry.HasLineOfSight(235, 246, cell => cell == 235 || cell == 246));

            // Three across and one down: both axes are due at once halfway, and the line steps
            // diagonally there instead of brushing a corner cell.
            Assert.Equal(new[] { 249, 250, 265 }, MapGeometry.SightLine(235, 265));
            Assert.Equal(new[] { 249, 263, 277, 292, 305, 320 }, MapGeometry.SightLine(235, 320));
        }
    }
}
