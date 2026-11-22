using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    [Collection("MapManager")]
    public sealed class SufokiaZaapiTests
    {
        private const long MerchantQuay = 90706177;

        private static void Load()
        {
            if (!MapManager.WalkableCells.ContainsKey(MerchantQuay)) MapManager.Initialize();
            Interactives.Initialize();
            Zaapis.Initialize();
        }

        [Fact]
        public void The_merchant_quay_element_is_a_Sufokia_zaapi()
        {
            Load();

            Assert.Contains(Zaapis.ElementsOn(MerchantQuay), element => element.Gfx == 34925);

            var network = Zaapis.NetworkOn(MerchantQuay);
            Assert.NotNull(network);
            Assert.Equal("sufokia", network!.City);
            Assert.Equal(6, network.Destinations.Count);
            Assert.Contains(network.Destinations, destination => destination.MapId == MerchantQuay);
            Assert.DoesNotContain(network.Destinations, destination => destination.MapId == 95424000);
        }
    }
}
