using System.Linq;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The two shapes the Tymador's Fusil and Colado are written on: the perpendicular line
    /// and the ring, with the cells of the fights they were reported from.
    /// </summary>
    public class ZoneShapeTests
    {
        /// <summary>
        /// Fusil from 261 at 259 is a diagonal cast in map coordinates -- (18,0) to (16,-2) --
        /// and its bar of two runs along the other diagonal: (17,-3), (18,-4) one way and
        /// (15,-1), (14,0) the other, which is cell 203, where the Ocra stood and took nothing.
        /// </summary>
        [Fact]
        public void The_perpendicular_line_crosses_a_diagonal_cast_along_the_other_diagonal()
        {
            var cells = Zone.Casillas(Zone.LineaPerpendicular, 2, desde: 261, centro: 259);

            Assert.Equal(5, cells.Count);
            Assert.Contains(259, cells);
            Assert.Contains(203, cells);
            Assert.Contains(MapGeometry.PointToCell(15, -1), cells);
            Assert.Contains(MapGeometry.PointToCell(17, -3), cells);
            Assert.Contains(MapGeometry.PointToCell(18, -4), cells);
            Assert.DoesNotContain(261, cells);
        }

        /// <summary>A straight cast gets the bar across it, and a bar of two is five cells.</summary>
        [Fact]
        public void The_perpendicular_line_crosses_a_straight_cast()
        {
            var (x, y) = MapGeometry.CellToPoint(288);
            int aimed = MapGeometry.PointToCell(x + 3, y);

            var cells = Zone.Casillas(Zone.LineaPerpendicular, 2, desde: 288, centro: aimed);

            Assert.Equal(new[] { aimed, MapGeometry.PointToCell(x + 3, y + 1), MapGeometry.PointToCell(x + 3, y + 2),
                                 MapGeometry.PointToCell(x + 3, y - 1), MapGeometry.PointToCell(x + 3, y - 2) }
                             .OrderBy(c => c),
                         cells.OrderBy(c => c));
        }

        /// <summary>
        /// Colado is a ring of two: the eight cells exactly two away, the centre and the
        /// neighbours left out. In its capture the bomb it mirrors is two from the centre in
        /// every cast.
        /// </summary>
        [Fact]
        public void The_ring_is_the_cells_exactly_that_far_and_not_the_centre()
        {
            var cells = Zone.Casillas(Zone.Anillo, 2, desde: 301, centro: 301);

            Assert.Equal(8, cells.Count);
            Assert.All(cells, c => Assert.Equal(2, MapGeometry.Distance(301, c)));
            Assert.DoesNotContain(301, cells);
            Assert.Contains(274, cells);
            Assert.Contains(272, cells);
        }
    }
}
