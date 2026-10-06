using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Symmetric teleports and the memory of where one went through.
    /// </summary>
    /// <remarks>
    /// Four effects, 54 class spells: 1104 relative to the target, 1105 relative to the
    /// caster, 1106 relative to the targeted cell, and 1100, which is not symmetric but
    /// undoes the last movement.
    ///
    /// The easy part to get wrong is the geometry: the Dofus grid runs diagonally, so
    /// the reflection is NOT arithmetic on the cell number. It has to go through coordinates.
    /// </remarks>
    public class TeleportSymmetryTests
    {
        [Fact]
        public void El_reflejo_deja_al_otro_lado_y_a_la_misma_distancia()
        {
            // With the pivot in the middle, going and coming back has to return to the starting point.
            const int pivote = 300;
            foreach (int desde in new[] { 285, 286, 314, 315, 271, 329 })
            {
                int alOtroLado = MapGeometry.Reflejar(desde, pivote);
                if (alOtroLado < 0) continue;

                Assert.NotEqual(desde, alOtroLado);
                Assert.Equal(MapGeometry.Distance(desde, pivote),
                             MapGeometry.Distance(pivote, alOtroLado));

                // And it is an involution: reflecting the reflection returns the original.
                Assert.Equal(desde, MapGeometry.Reflejar(alOtroLado, pivote));
            }
        }

        [Fact]
        public void Reflejarse_sobre_uno_mismo_no_mueve()
        {
            Assert.Equal(300, MapGeometry.Reflejar(300, 300));
        }

        [Fact]
        public void Fuera_del_tablero_devuelve_menos_uno_en_vez_de_una_casilla_inventada()
        {
            // With the pivot against the edge, the reflection goes out. Better to say it cannot be done than
            // to send someone to a cell that does not exist.
            Assert.Equal(-1, MapGeometry.Reflejar(-5, 300));
            Assert.Equal(-1, MapGeometry.Reflejar(300, -5));
        }

        [Fact]
        public void El_luchador_se_acuerda_de_donde_venia()
        {
            var quien = new Fighter { Id = 1, MaxHP = 100, CurrentHP = 100, CellId = 200 };

            // Without having moved yet there is nowhere to go back to.
            Assert.Equal(-1, quien.CasillaAnterior);

            quien.MoverA(315);
            Assert.Equal(315, quien.CellId);
            Assert.Equal(200, quien.CasillaAnterior);

            quien.MoverA(330);
            Assert.Equal(315, quien.CasillaAnterior);

            // Moving to where one already is does not count as a movement.
            quien.MoverA(330);
            Assert.Equal(315, quien.CasillaAnterior);
        }
    }
}
