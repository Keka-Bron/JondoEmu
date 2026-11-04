using Jondo.Unity.Server;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Which maps' coordinates are a place on the world map: [0,0] is Amakna's village and also what
    /// the Koliseo's arenas carry for having none, and a hunt's sighting sent the hunter to the
    /// village after a prey fighting in an arena.
    /// </summary>
    [Collection("MapManager")]
    public sealed class WorldMapPlaceTests
    {
        [Fact]
        public void A_real_zero_zero_has_a_neighbour_one_step_away_and_an_arena_has_not()
        {
            if (MapManager.GetMapInfo(88212247) == null) MapManager.Initialize();
            if (MapManager.GetMapInfo(88212247) == null) return;   // no world data

            Assert.True(MapManager.OnTheWorldMap(88212247));     // Amakna's village, [0,0]
            Assert.False(MapManager.OnTheWorldMap(233313286));   // «Ecaflip City (Duel)», [0,0]
            Assert.True(MapManager.OnTheWorldMap(84806403));     // [-5,-25]
            Assert.False(MapManager.OnTheWorldMap(1));           // no map at all
        }
    }
}
