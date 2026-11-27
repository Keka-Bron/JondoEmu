using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Movement;

/// <summary>
/// A border whose MapScrolls neighbour is not a map of the world falls back to the coordinates:
/// the right of 147851275 [-10,-4] names 147851787, which does not exist, and Sramvil's
/// [-9,-4] is 135940. Before, the character stayed on the border for good.
/// </summary>
[Collection("MapManager")]
public sealed class MissingScrollNeighbourTests
{
    private const long Here = 147851275;
    private const long WrittenButMissing = 147851787;
    private const long NextDoor = 135940;

    [Fact]
    public void A_missing_scroll_neighbour_falls_back_to_the_coordinates()
    {
        if (MapManager.GetMapInfo(Here) == null) MapManager.Initialize();
        if (MapManager.GetMapInfo(Here) == null || MapManager.GetMapInfo(NextDoor) == null) return;   // no world data
        Assert.Null(MapManager.GetMapInfo(WrittenButMissing));

        int rightEdge = 20 * 14 + 13;
        Assert.Equal(NextDoor, WorldMoveHandler.NeighbourPast(Here, rightEdge));
    }
}
