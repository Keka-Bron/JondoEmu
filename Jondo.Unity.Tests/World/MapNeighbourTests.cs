using System.Collections.Generic;
using Jondo.Unity.Server;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Which map you are allowed to walk onto, and which you are not.
    /// </summary>
    /// <remarks>
    /// The map-change request carries the destination the client wants. The server used to write it
    /// into the session and save it to the database without asking whether the two maps touch, so
    /// one edited packet reached any of the 15,360 maps in the game: past a zaap nobody had
    /// unlocked, into a dungeon, on top of anybody. Nothing downstream could notice, because from
    /// that point on the session honestly believed it was there.
    ///
    /// The real neighbours are laid out around Incarnam's (1,-2), which is where Anta Brok stands
    /// and the map every quest here starts from.
    /// </remarks>
    public class MapNeighbourTests
    {
        private const long Centre = 154010883;

        private static void Lay(long right = 0, long bottom = 0, long left = 0, long top = 0)
        {
            MapManager.ScrollActions = new Dictionary<long, MapScrollAction>
            {
                [Centre] = new MapScrollAction
                {
                    MapId = Centre,
                    RightMapId = right,
                    BottomMapId = bottom,
                    LeftMapId = left,
                    TopMapId = top,
                },
            };
        }

        [Theory]
        [InlineData(1)]   // right
        [InlineData(2)]   // bottom
        [InlineData(3)]   // left
        [InlineData(4)]   // top
        public void Each_of_the_four_edges_is_a_way_out(int edge)
        {
            const long there = 154010884;
            Lay(right:  edge == 1 ? there : 0,
                bottom: edge == 2 ? there : 0,
                left:   edge == 3 ? there : 0,
                top:    edge == 4 ? there : 0);

            Assert.True(MapManager.IsNeighbour(Centre, there));
        }

        [Fact]
        public void A_map_on_the_other_side_of_the_world_is_not()
        {
            // The whole point. 88061954 is Astrub; from Incarnam you get there by zaap, not by
            // walking, and certainly not by saying so in a packet.
            Lay(right: 154010884, bottom: 154010885);

            Assert.False(MapManager.IsNeighbour(Centre, 88061954));
        }

        [Fact]
        public void An_edge_that_leads_nowhere_is_not_a_way_out()
        {
            // Unset edges are stored as 0, and 0 is also what an empty request parses to. Without
            // the toMapId <= 0 guard, asking to move to map 0 from a map with any closed edge
            // would have been allowed -- and every border map in the game has closed edges.
            Lay(right: 154010884);

            Assert.False(MapManager.IsNeighbour(Centre, 0));
            Assert.False(MapManager.IsNeighbour(Centre, -5));
        }

        [Fact]
        public void Standing_still_is_not_a_transition()
        {
            Lay(right: 154010884);
            Assert.False(MapManager.IsNeighbour(Centre, Centre));
        }

        [Fact]
        public void A_map_with_no_row_lets_nobody_out()
        {
            // Refusing here is only safe because the data says it never happens: 15,360 maps with a
            // position and zero without a scroll row. If that ever stops being true, this test still
            // passes and players get stuck, so the number is written down in MapManager.IsNeighbour
            // where whoever imports the next map file will read it.
            MapManager.ScrollActions = new Dictionary<long, MapScrollAction>();

            Assert.False(MapManager.IsNeighbour(Centre, 154010884));
        }

        [Fact]
        public void A_null_row_does_not_throw()
        {
            MapManager.ScrollActions = new Dictionary<long, MapScrollAction> { [Centre] = null! };

            Assert.False(MapManager.IsNeighbour(Centre, 154010884));
        }
    }
}
