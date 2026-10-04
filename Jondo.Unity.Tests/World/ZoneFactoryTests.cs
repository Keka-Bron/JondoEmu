using System;
using System.Linq;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The shapes as the client's zone factory builds them (gru::blgy and the shape classes of the
    /// 3.6.10 GameAssembly): which letter is which class, and the cells each class walks.
    /// </summary>
    public class ZoneFactoryTests
    {
        private const int Centre = 301;

        private static (int X, int Y) Offset(int cell)
        {
            var (cx, cy) = MapGeometry.CellToPoint(Centre);
            var (x, y) = MapGeometry.CellToPoint(cell);
            return (x - cx, y - cy);
        }

        /// <summary>The '+' is the four diagonal rays and the centre, not the eight.</summary>
        [Fact]
        public void The_plus_is_the_diagonal_cross()
        {
            var cells = Zone.Casillas(Zone.DiagonalCross, 2, 260, Centre);

            Assert.Equal(9, cells.Count);
            Assert.Contains(Centre, cells);
            Assert.All(cells.Where(c => c != Centre), c =>
            {
                var (x, y) = Offset(c);
                Assert.Equal(Math.Abs(x), Math.Abs(y));
            });
        }

        /// <summary>The '#' is the same cross without its centre, and not a filled square.</summary>
        [Fact]
        public void The_sharp_is_the_diagonal_cross_without_its_centre()
        {
            var cells = Zone.Casillas(Zone.DiagonalCrossWithoutCentre, 1, 260, Centre);

            Assert.Equal(4, cells.Count);
            Assert.DoesNotContain(Centre, cells);
            Assert.All(cells, c => Assert.Equal(2, MapGeometry.Distance(Centre, c)));
        }

        /// <summary>
        /// A cross's param2 counts steps along its rays: #2/2 keeps the second diagonal step, four
        /// cells away, where a distance of two would have kept the first.
        /// </summary>
        [Fact]
        public void The_inner_edge_of_a_cross_counts_steps_along_the_ray()
        {
            var cells = Zone.Casillas(Zone.DiagonalCrossWithoutCentre, 2, 260, Centre, minimo: 2);

            Assert.Equal(4, cells.Count);
            Assert.All(cells, c => Assert.Equal(4, MapGeometry.Distance(Centre, c)));
        }

        /// <summary>The '*' is the eight rays with the centre.</summary>
        [Fact]
        public void The_star_is_the_eight_rays()
        {
            var cells = Zone.Casillas(Zone.Star, 1, 260, Centre);

            Assert.Equal(9, cells.Count);
            Assert.Contains(Centre, cells);
            Assert.All(cells, c =>
            {
                var (x, y) = Offset(c);
                Assert.True(Math.Abs(x) <= 1 && Math.Abs(y) <= 1);
            });
        }

        /// <summary>The Q has no centre, whatever its param2: a bare Q1 is the four cells around.</summary>
        [Fact]
        public void The_q_never_holds_its_centre()
        {
            var cells = Zone.Casillas(Zone.CruzRecta, 1, 260, Centre);

            Assert.Equal(4, cells.Count);
            Assert.DoesNotContain(Centre, cells);
        }

        /// <summary>The I is every cell at param1 or more: I0 the whole map, I2 all but the near five.</summary>
        [Fact]
        public void The_i_is_everything_beyond_its_radius()
        {
            Assert.Equal(MapGeometry.MaxCells, Zone.Casillas(Zone.OutsideCircle, 0, 260, Centre).Count);

            var far = Zone.Casillas(Zone.OutsideCircle, 2, 260, Centre);
            Assert.Equal(MapGeometry.MaxCells - 5, far.Count);
            Assert.All(far, c => Assert.True(MapGeometry.Distance(Centre, c) >= 2));
        }

        /// <summary>The G is the filled square; the W the same square without its two diagonals.</summary>
        [Fact]
        public void The_g_is_the_square_and_the_w_the_square_without_diagonals()
        {
            var square = Zone.Casillas(Zone.Square, 1, 260, Centre);
            Assert.Equal(9, square.Count);
            Assert.Contains(Centre, square);

            var hollow = Zone.Casillas(Zone.SquareWithoutDiagonals, 2, 260, Centre);
            Assert.Equal(25 - 9, hollow.Count);
            Assert.DoesNotContain(Centre, hollow);
            Assert.All(hollow, c =>
            {
                var (x, y) = Offset(c);
                Assert.NotEqual(Math.Abs(x), Math.Abs(y));
            });
        }

        /// <summary>The '/' is the line class under another letter.</summary>
        [Fact]
        public void The_slash_is_the_line()
        {
            Assert.Equal(Zone.Casillas(Zone.Linea, 3, 245, Centre), Zone.Casillas(Zone.DiagonalLine, 3, 245, Centre));
        }

        /// <summary>
        /// The line from the caster starts param1 cells off it and is param2 long: l1/5 not stopping
        /// at the target runs five cells past the caster whatever was aimed at.
        /// </summary>
        [Fact]
        public void The_line_from_the_caster_runs_its_length_unless_it_stops_at_the_target()
        {
            var (x, y) = MapGeometry.CellToPoint(Centre);
            int aimed = MapGeometry.PointToCell(x + 2, y);

            var stops = Zone.Casillas(Zone.Segmento, 1, Centre, aimed, minimo: 5);
            Assert.Equal(new[] { MapGeometry.PointToCell(x + 1, y), aimed }, stops);

            var runs = Zone.Casillas(Zone.Segmento, 1, Centre, aimed, minimo: 5, stopAtTarget: false);
            Assert.Equal(Enumerable.Range(1, 5).Select(i => MapGeometry.PointToCell(x + i, y)), runs);
        }
    }
}
