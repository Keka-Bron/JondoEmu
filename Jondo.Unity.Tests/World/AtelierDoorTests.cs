using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    [Collection("MapManager")]
    public sealed class AtelierDoorTests
    {
        [Fact]
        public void The_cordwainer_workshop_is_entered_from_the_real_door_and_has_a_way_back_out()
        {
            if (!MapManager.WalkableCells.ContainsKey(90707201)) MapManager.Initialize();
            TeleportManager.Initialize();

            Assert.True(TeleportManager.TryGet(90707201, 538502, out var entrance));
            Assert.Equal(91750923, entrance.DestinationMapId);
            Assert.True(TeleportManager.TryGet(91750923, 524220, out var exit));
            Assert.Equal(90707201, exit.DestinationMapId);
            Assert.Equal(158, exit.DestinationCellId);
            Assert.Contains(158, MapManager.WalkableCells[90707201]);
            Assert.True(TeleportManager.TryGet(90708226, 524219, out var market));
            Assert.Equal(91750927, market.DestinationMapId);
            Assert.True(TeleportManager.TryGet(91750927, 538510, out var marketExit));
            Assert.Equal(90708226, marketExit.DestinationMapId);
        }
    }
}
