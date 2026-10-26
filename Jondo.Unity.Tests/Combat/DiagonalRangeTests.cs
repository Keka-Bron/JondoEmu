using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A spell cast diagonally counts each diagonal step as one, unless its diagonal is
    /// proportional: the client draws its range as a star or a diagonal cross (flh.bgic). The
    /// Gatito rabioso's «Alma Felina», 1 to 1 in line and diagonal, is offered on its four diagonal
    /// neighbours, two steps away.
    /// </summary>
    public class DiagonalRangeTests
    {
        private static int At(int cell, int dx, int dy)
        {
            var (x, y) = MapGeometry.CellToPoint(cell);
            return MapGeometry.PointToCell(x + dx, y + dy);
        }

        [Fact]
        public void A_diagonal_neighbour_is_one_away_for_a_diagonal_spell()
        {
            int diagonal = At(367, 1, -1);
            Assert.Equal(2, MapGeometry.Distance(367, diagonal));
            Assert.Equal(1, MapGeometry.CastDistance(367, diagonal, diagonal: true, proportionalDiagonal: false));
            Assert.Equal(3, MapGeometry.CastDistance(367, At(367, 3, 3), diagonal: true, proportionalDiagonal: false));
        }

        [Fact]
        public void Off_the_diagonal_or_proportional_it_is_the_walking_distance()
        {
            int diagonal = At(367, 1, -1);
            Assert.Equal(2, MapGeometry.CastDistance(367, diagonal, diagonal: false, proportionalDiagonal: false));
            Assert.Equal(2, MapGeometry.CastDistance(367, diagonal, diagonal: true, proportionalDiagonal: true));
            Assert.Equal(3, MapGeometry.CastDistance(367, At(367, 2, 1), diagonal: true, proportionalDiagonal: false));
        }
    }
}
