using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Doors land where the official server put the character: the cell in the new map's contents,
    /// from packet captures of walks through the world (2026-11-17 to 11-22). Doors used two or more
    /// times landed on the same cell every time.
    /// </summary>
    [Collection("MapManager")]
    public sealed class MeasuredDoorLandingTests
    {
        [Theory]
        [InlineData(99090955, 474855, 99091979, 401)]    // Jondo had no route
        [InlineData(88083734, 535723, 183763968, 477)]   // Jondo had no route
        [InlineData(99357706, 474895, 99357708, 329)]    // the reverse-element route landed elsewhere
        [InlineData(99095049, 474853, 99094025, 402)]    // the reverse-element route landed elsewhere
        [InlineData(99356682, 474891, 99357708, 343)]    // the exact-element route landed on 356
        [InlineData(88212245, 472911, 99356672, 383)]    // the exact-element route landed on 369
        [InlineData(217057540, 540348, 217055492, 315)]  // the inferred pairing went to another map
        [InlineData(217055492, 540934, 217057540, 319)]  // the inferred pairing went to another map
        [InlineData(188743685, 515978, 188482052, 518)]  // an authored row: the mine mouth, 478 before
        [InlineData(191104002, 515308, 192415750, 396)]  // an authored row: Astrub's bank, 410 before
        public void The_door_lands_where_the_official_server_landed(long map, int element, long toMap, int toCell)
        {
            if (MapManager.GetMapInfo(map) == null) MapManager.Initialize();
            Assert.NotNull(MapManager.GetMapInfo(map));
            TeleportManager.Initialize();

            Assert.True(TeleportManager.TryGet(map, element, out var route));
            Assert.Equal((toMap, toCell), (route.DestinationMapId, route.DestinationCellId));
            // On the map: the roleplay list trims the borders (it places monsters), where doors often
            // land, so the whole map's walkable cells count too.
            Assert.True(MapManager.WalkableCells[toMap].Contains(toCell)
                        || MapManager.GetFightWalkable(toMap).Contains(toCell));
        }
    }
}
