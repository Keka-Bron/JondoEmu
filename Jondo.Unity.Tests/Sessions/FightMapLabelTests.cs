using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Sessions
{
    /// <summary>
    /// The jrk names the fight's place: the client looks its f2 up as a subarea and writes the
    /// coordinates of its f3 beside it.
    /// </summary>
    public sealed class FightMapLabelTests
    {
        [Fact]
        public void The_subarea_and_the_coordinates_travel_with_the_map()
            => Assert.Equal("10071a0d08ffffffffffffffffff0110022005",
                            System.Convert.ToHexString(FightProtocol.BuildFightMap(5, 7, -1, 2)).ToLowerInvariant());

        [Fact]
        public void Coordinates_at_the_origin_still_go_present()
            => Assert.Equal("100a1a002005",
                            System.Convert.ToHexString(FightProtocol.BuildFightMap(5, 10, 0, 0)).ToLowerInvariant());
    }
}
