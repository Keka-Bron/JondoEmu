using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A pull stops as soon as it would reach the centre, and does not overshoot.
    /// </summary>
    /// <remarks>
    /// Without this the Rogue's Imantación —which pulls his bombs SIX cells— crossed the point
    /// and left them on the other side. And since the spell pulls twice, the second brought them back:
    /// in the log the dance is seen, bomb -5 from 272 to 185 and from 185 back to 272.
    /// </remarks>
    public class PullTests
    {
        private static int Desde(int celda, int dx, int dy)
        {
            var (x, y) = MapGeometry.CellToPoint(celda);
            return MapGeometry.PointToCell(x + dx, y + dy);
        }

        [Fact]
        public void Un_tiron_largo_se_queda_pegado_al_centro()
        {
            int centro = 270;
            int quienTira = Desde(centro, 0, -3);
            int bomba = Desde(centro, 0, 4);

            var r = Zone.Push(centro, quienTira, bomba, casillas: -6,
                              pisables: null, ocupadas: null);

            Assert.Equal(Desde(centro, 0, 1), r.ToCell);
            Assert.NotEqual(centro, r.ToCell);
        }

        [Fact]
        public void Un_tiron_corto_llega_donde_le_toca()
        {
            int centro = 270;
            int bomba = Desde(centro, 0, 5);

            var r = Zone.Push(centro, centro, bomba, casillas: -2,
                              pisables: null, ocupadas: null);

            Assert.Equal(Desde(centro, 0, 3), r.ToCell);
        }

        [Fact]
        public void Un_empujon_no_mira_el_centro_para_nada()
        {
            int centro = 270;
            int bicho = Desde(centro, 0, 1);

            var r = Zone.Push(centro, centro, bicho, casillas: 3,
                              pisables: null, ocupadas: null);

            Assert.Equal(Desde(centro, 0, 4), r.ToCell);
        }
    }
}
