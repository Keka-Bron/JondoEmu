using System.IO;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Gremia, la vendedora del Templo de los Gremios, y la capa de tiendas escritas a mano.
    /// </summary>
    /// <remarks>
    /// Medido: su kbd en la captura de fundar «Jondo» lista la gremialogema a 30.000, el libro
    /// «Acerca de los gremios» a 500 y el escudo de gremio a 100.000. Aquí la gremialogema vale un
    /// kama, que es decisión de este servidor, y los otros dos lo que la captura dice.
    /// </remarks>
    public class GuildVendorTests
    {
        [Fact]
        public void Gremia_sells_the_guildalogem_for_one_kama()
        {
            NpcShops.Forget();
            NpcShops.ApplyAuthored(Paths.ContentFile(NpcShops.AuthoredFile));

            const int gremia = 7580;
            Assert.True(NpcShops.Sells(gremia));
            Assert.Equal(new[] { GuildHandler.GuildalogemTemplate, 30810, 13240 }, NpcShops.CatalogueOf(gremia));
            Assert.Equal(1, NpcShops.PriceOf(GuildHandler.GuildalogemTemplate));
            Assert.Equal(500, NpcShops.PriceOf(30810));
            Assert.Equal(100000, NpcShops.PriceOf(13240));

            NpcShops.Forget();
        }

        /// <summary>Un fichero que no está no rompe nada, y uno a medias tampoco.</summary>
        [Fact]
        public void A_missing_or_empty_file_changes_nothing()
        {
            NpcShops.Forget();
            NpcShops.ApplyAuthored(Path.Combine(Path.GetTempPath(), "no-existe-" + Path.GetRandomFileName()));
            Assert.Equal(0, NpcShops.Count);

            string file = Path.GetTempFileName();
            try
            {
                File.WriteAllText(file, "{ \"shops\": [ { \"npc\": 1, \"items\": [] }, { \"npc\": 2 } ] }");
                NpcShops.ApplyAuthored(file);
                Assert.Equal(0, NpcShops.Count);
            }
            finally
            {
                File.Delete(file);
                NpcShops.Forget();
            }
        }
    }
}
