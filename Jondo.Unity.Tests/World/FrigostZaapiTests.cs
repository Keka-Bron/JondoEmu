using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    [Collection("MapManager")]
    public sealed class FrigostZaapiTests
    {
        private const long CarversWorkshop = 54169896;
        private const long VillageZaap = 54172969;

        private static void Load()
        {
            if (!MapManager.WalkableCells.ContainsKey(CarversWorkshop)) MapManager.Initialize();
            Interactives.Initialize();
            Zaapis.Initialize();
        }

        [Fact]
        public void The_carvers_workshop_element_is_a_Frigost_zaapi()
        {
            Load();

            Assert.Contains(Zaapis.ElementsOn(CarversWorkshop), element => element.Gfx == 70914);

            var network = Zaapis.NetworkOn(CarversWorkshop);
            Assert.NotNull(network);
            Assert.Equal("frigost", network!.City);
            Assert.Equal(7, network.Destinations.Count);
            Assert.Contains(network.Destinations, destination => destination.MapId == CarversWorkshop);
            Assert.Contains(network.Destinations, destination => destination.MapId == VillageZaap);
            Assert.All(network.Destinations, destination => Assert.Equal(601, destination.SubAreaId));
            Assert.All(network.Destinations,
                destination => Assert.Contains(Zaapis.ElementsOn(destination.MapId), element => element.Gfx == 70914));
        }
    }
}
