using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Passages the official server refused with its own message, Bonta's guild hall zaap, and two
    /// doors measured on the way (packet captures, 2026-11-20/21).
    /// </summary>
    [Collection("MapManager")]
    public sealed class OfficialRefusalTests
    {
        private static void Load(long map)
        {
            if (MapManager.GetMapInfo(map) == null) MapManager.Initialize();
            Assert.NotNull(MapManager.GetMapInfo(map));
            Interactives.Initialize();
            TeleportManager.Initialize();
        }

        /// <summary>The holes for the shrunk: the client's worldgraph criterion (the wand worn, or the
        /// reducer), and refused with 449 as an error to a character with neither.</summary>
        [Theory]
        [InlineData(192939014, 515804)]
        [InlineData(192545798, 515384)]
        public void The_holes_refuse_with_449_as_an_error(long map, int element)
        {
            Load(map);
            Assert.True(TeleportManager.TryGet(map, element, out var hole));
            Assert.Equal("(POE15990,1|PO>31494,0)", hole.Criterion);
            Assert.Equal((449, 1), (hole.Refusal, hole.RefusalType));
        }

        [Fact]
        public void Bonta_s_stair_for_the_highest_ranks_refuses_with_811()
        {
            Load(213910536);
            Assert.True(TeleportManager.TryGet(213910536, 521486, out var stair));
            Assert.Equal((811, 0), (stair.Refusal, stair.RefusalType));
            Assert.False(string.IsNullOrEmpty(stair.Criterion));
        }

        [Fact]
        public void Bonta_s_guild_hall_zaap_is_no_door()
        {
            Load(217055492);
            Assert.False(TeleportManager.TryGet(217055492, 540446, out _));
        }

        [Theory]
        [InlineData(192417794, 537305, 191105028, 215)]   // out of Fifi de Lificha's room
        [InlineData(193200131, 515700, 193200131, 21)]    // a step to cell 21 of the same map
        public void The_door_lands_where_the_official_server_landed(long map, int element, long toMap, int toCell)
        {
            Load(map);
            Assert.True(TeleportManager.TryGet(map, element, out var route));
            Assert.Equal((toMap, toCell), (route.DestinationMapId, route.DestinationCellId));
        }
    }
}
