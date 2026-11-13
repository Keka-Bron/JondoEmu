using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// How much a zone's blow loses with distance, as the 3.6.10.11 client reckons it
    /// (gru::blgz picks the measure by shape, grz::blil applies it).
    /// </summary>
    public class ZoneFalloffTests
    {
        private const int Centre = 301;

        private static int At(int dx, int dy)
        {
            var (x, y) = MapGeometry.CellToPoint(Centre);
            return MapGeometry.PointToCell(x + dx, y + dy);
        }

        /// <summary>
        /// The Tigredón's Salto del Grillete, a ring of two: every cell it has is two away, and it
        /// hits all of them in full.
        /// </summary>
        [Fact]
        public void A_ring_hits_its_whole_ring_in_full()
        {
            foreach (int cell in Zone.Casillas(Zone.Anillo, 2, Centre, Centre))
                Assert.Equal(0, Zone.FalloffCells(Zone.Anillo, 2, 0, Centre, Centre, cell));
        }

        [Theory]
        [InlineData(Zone.TodoElMapa, 0)]
        [InlineData(Zone.WholeMap, 0)]
        [InlineData(Zone.Circulo, 63)]
        public void The_whole_map_does_not_fall_off(int shape, int size)
            => Assert.Equal(0, Zone.FalloffCells(shape, size, 0, Centre, Centre, At(4, 3)));

        [Fact]
        public void A_circle_with_a_hole_counts_from_its_inner_edge()
        {
            Assert.Equal(0, Zone.FalloffCells(Zone.Circulo, 3, 1, Centre, Centre, At(1, 0)));
            Assert.Equal(2, Zone.FalloffCells(Zone.Circulo, 3, 1, Centre, Centre, At(2, 1)));
            Assert.Equal(3, Zone.FalloffCells(Zone.Circulo, 3, 0, Centre, Centre, At(2, 1)));
        }

        [Fact]
        public void A_square_measures_its_larger_gap()
            => Assert.Equal(2, Zone.FalloffCells(Zone.Square, 2, 0, Centre, Centre, At(2, 2)));

        [Fact]
        public void A_diagonal_cross_takes_half_the_distance()
            => Assert.Equal(1, Zone.FalloffCells(Zone.DiagonalCross, 2, 0, Centre, Centre, At(1, 1)));

        /// <summary>Cast along the walked x axis, a cone counts the gap on that axis only.</summary>
        [Fact]
        public void A_cone_measures_along_the_way_it_was_cast()
            => Assert.Equal(2, Zone.FalloffCells(Zone.Cone, 3, 0, At(-1, 0), Centre, At(2, 1)));

        [Theory]
        [InlineData(1, 90)]
        [InlineData(2, 80)]
        [InlineData(4, 60)]
        [InlineData(6, 60)]
        public void The_damage_lost_adds_up_rather_than_compounding(int cells, int left)
        {
            var row = new SpellEffect { Forma = Zone.Circulo, Tamano = 6, PasoDeCaida = 10, TopeDeCaida = 4 };
            Assert.Equal(left, EffectEngine.ConLaCaidaDeLaZona(100, row, cells));
        }

        [Fact]
        public void A_count_of_zero_loses_nothing_and_nothing_loses_more_than_all()
        {
            Assert.Equal(100, EffectEngine.ConLaCaidaDeLaZona(100,
                new SpellEffect { PasoDeCaida = 10, TopeDeCaida = 0 }, 3));
            Assert.Equal(0, EffectEngine.ConLaCaidaDeLaZona(100,
                new SpellEffect { PasoDeCaida = 50, TopeDeCaida = 4 }, 3));
        }
    }
}
