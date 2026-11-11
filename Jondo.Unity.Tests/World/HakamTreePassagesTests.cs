using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Hakam tree's lianas: where Giny's catalogue and the 2.73 world graph send one element to
    /// different maps, the graph's wins (TeleportManager). Giny's sent two of them to Amakna.
    /// </summary>
    public class HakamTreePassagesTests
    {
        private static bool WorldLoaded()
        {
            if (!MapManager.WalkableCells.ContainsKey(25692680)) MapManager.Initialize();
            if (MapManager.GetMapInfo(25692680) == null || MapManager.GetMapInfo(63964931) == null) return false;
            Interactives.Initialize();
            TeleportManager.Initialize();
            return true;
        }

        [Theory]
        [InlineData(25692680, 434036, 25691146)]   // the branch [-52,17] down to its lower half, not Amakna [3,-4]
        [InlineData(25694213, 434024, 63964931)]   // the branch [-53,17] back up to the canopy, not Amakna [9,6]
        [InlineData(63964931, 458463, 25694213)]   // the canopy [-53,17] down to the branch, not into the trunk
        public void A_liana_leads_where_the_world_graph_says(long map, int element, long to)
        {
            if (!WorldLoaded()) return;   // no world data

            Assert.True(TeleportManager.TryGet(map, element, out var route));
            Assert.Equal(to, route.DestinationMapId);
        }
    }
}
