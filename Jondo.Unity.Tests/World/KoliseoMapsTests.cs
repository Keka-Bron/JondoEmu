using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The koliseo arenas: that there are some, and that the one chosen has room.
    /// </summary>
    /// <remarks>
    /// The Duelo ones are really small — 37 of 85 with a single cell per side — so
    /// choosing «the subarea it gets» would put a three versus three where one fits. It is chosen by
    /// capacity, and that is what these tests pin.
    /// </remarks>
    public class KoliseoMapsTests
    {
        [Fact]
        public void Estan_las_arenas()
        {
            // 441 maps in the Koliseo's three subareas: one without placement cells, 46 without
            // a name -- not arenas the game fights on.
            Assert.Equal(394, KoliseoMaps.Count);
        }

        /// <summary>
        /// No arena whose fight grid is not its board: the three whose 522 cells all walk in a
        /// fight let a fighter walk out into the void.
        /// </summary>
        [Fact]
        public void No_arena_lets_a_fighter_walk_off_its_board()
        {
            // Read from the file itself: MapManager.Initialize rebuilds shared state other test
            // classes lean on.
            using var cells = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(Jondo.Unity.Launcher.Paths.FightCellsJson));
            int Walks(long map) => cells.RootElement.GetProperty(map.ToString()).GetProperty("f").GetArrayLength();

            foreach (long broken in new long[] { 230170117, 230432261, 230694405 })
                Assert.Equal(522, Walks(broken));
            for (int i = 0; i < 300; i++)
            {
                var arena = KoliseoMaps.PickFor(1)!;
                Assert.True(Walks(arena.MapId) < 400, $"{arena.MapId} walks {Walks(arena.MapId)} cells");
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void La_que_se_elige_tiene_sitio_para_los_dos_bandos(int teamSize)
        {
            Assert.True(KoliseoMaps.CountFor(teamSize) > 0,
                        $"no hay ni una arena para {teamSize} por bando");

            // A hundred times, since it is chosen at random and a single roll proves nothing.
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
            // It is not obvious: it is what says the capacity filter does something. If it gave
            // the same for one and for three, it would be choosing by subarea without looking at the size.
            Assert.True(KoliseoMaps.CountFor(1) > KoliseoMaps.CountFor(3));
        }

        [Fact]
        public void Un_equipo_imposible_no_devuelve_arena()
        {
            // None goes beyond six per side, so eight fits in none. Returning null is
            // right: the fight is then set up in the usual arena.
            Assert.Equal(0, KoliseoMaps.CountFor(8));
            Assert.Null(KoliseoMaps.PickFor(8));
        }
    }
}
