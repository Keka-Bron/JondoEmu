using System.IO;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    [Collection("npc shops")]
    public class NpcMarketConditionsTests
    {
        [Theory]
        [InlineData(7047, "PG=12")]
        [InlineData(7053, "Qf=2406")]
        [InlineData(23844, "Qf=2158")]
        [InlineData(24021, "Qa=2200|Qf=2200")]
        public void Urakoru_checks_purchase_conditions_when_listing_and_buying(int gid, string criterion)
        {
            NpcShops.Forget();
            try
            {
                NpcShops.ApplyAuthored(Paths.ContentFile(NpcShops.AuthoredFile));

                Assert.Contains(gid, NpcShops.CatalogueOf(5992));
                Assert.DoesNotContain(gid, NpcShops.CatalogueFor(5992, c => c != criterion));
                Assert.False(NpcShops.CanBuy(5992, gid, c => c != criterion));
                Assert.True(NpcShops.CanBuy(5992, gid, _ => true));
                Assert.Contains(23180, NpcShops.CatalogueFor(5992, _ => false));
                Assert.False(NpcShops.CanBuy(5992, 6660, _ => true));
            }
            finally { NpcShops.Forget(); }
        }

        [Fact]
        public void Conditions_are_vendor_specific_and_replaced_with_the_catalogue()
        {
            string file = Path.GetTempFileName();
            NpcShops.Forget();
            try
            {
                File.WriteAllText(file, """
                    {"shops": [
                      {"npc": 1, "alsoSoldBy": [2],
                       "items": [{"gid": 7047, "price": 20, "criterion": "PG=12"}]},
                      {"npc": 3, "items": [{"gid": 7047, "price": 20}]}
                    ]}
                    """);
                NpcShops.ApplyAuthored(file);
                Assert.False(NpcShops.CanBuy(1, 7047, _ => false));
                Assert.False(NpcShops.CanBuy(2, 7047, _ => false));
                Assert.True(NpcShops.CanBuy(3, 7047, _ => false));

                File.WriteAllText(file, """
                    {"shops": [
                      {"npc": 1, "alsoSoldBy": [2], "items": [{"gid": 7047, "price": 20}]}
                    ]}
                    """);
                NpcShops.ApplyAuthored(file);
                Assert.True(NpcShops.CanBuy(1, 7047, _ => false));
                Assert.True(NpcShops.CanBuy(2, 7047, _ => false));
            }
            finally
            {
                File.Delete(file);
                NpcShops.Forget();
            }
        }
    }
}
