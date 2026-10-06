using System.IO;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Gremia, the Guild Temple's seller, and the layer of hand-written shops.
    /// </summary>
    /// <remarks>
    /// Measured: her kbd in the capture of founding «Jondo» lists the guildalogem at 30,000, the book
    /// «Acerca de los gremios» at 500 and the guild shield at 100,000. Here the guildalogem costs one
    /// kama, which is this server's decision, and the other two what the capture says.
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

        /// <summary>A file that is not there breaks nothing, and a half-written one does not either.</summary>
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
