using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Las arenas del koliseo: que las haya, y que la que se elija tenga sitio.
    /// </summary>
    /// <remarks>
    /// Las de Duelo son pequeñas de verdad — 37 de 85 con una sola casilla por bando — así que
    /// elegir «la subárea que le toca» metería un tres contra tres donde cabe uno. Se elige por
    /// capacidad, y eso es lo que estas pruebas fijan.
    /// </remarks>
    public class KoliseoMapsTests
    {
        [Fact]
        public void Estan_las_arenas()
        {
            // 441 mapas en las tres subáreas de koliseo, uno de ellos sin casillas por bando.
            Assert.Equal(440, KoliseoMaps.Count);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void La_que_se_elige_tiene_sitio_para_los_dos_bandos(int teamSize)
        {
            Assert.True(KoliseoMaps.CountFor(teamSize) > 0,
                        $"no hay ni una arena para {teamSize} por bando");

            // Cien veces, que se elige al azar y una sola tirada no prueba nada.
            for (int i = 0; i < 100; i++)
            {
                var arena = KoliseoMaps.PickFor(teamSize);
                Assert.NotNull(arena);
                Assert.True(arena!.Blue.Count >= teamSize, $"{arena.MapId} sin sitio azul");
                Assert.True(arena.Red.Count >= teamSize, $"{arena.MapId} sin sitio rojo");
            }
        }

        [Fact]
        public void Cuanto_mas_grande_el_equipo_menos_arenas_valen()
        {
            // No es una obviedad: es lo que dice que el filtro por capacidad hace algo. Si diera
            // lo mismo para uno y para tres, estaría eligiendo por subárea sin mirar el tamaño.
            Assert.True(KoliseoMaps.CountFor(1) > KoliseoMaps.CountFor(3));
        }

        [Fact]
        public void Un_equipo_imposible_no_devuelve_arena()
        {
            // Ninguna pasa de seis por bando, así que ocho no cabe en ninguna. Devolver null es
            // lo correcto: el combate se monta entonces en el arena de siempre.
            Assert.Equal(0, KoliseoMaps.CountFor(8));
            Assert.Null(KoliseoMaps.PickFor(8));
        }
    }
}
