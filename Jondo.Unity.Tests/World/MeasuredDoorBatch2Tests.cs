using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Doors the official server walked a character through (packet captures, 2026-11-22 to 11-24):
    /// each lands where the character stood in the new map's contents.
    /// </summary>
    [Collection("MapManager")]
    public sealed class MeasuredDoorBatch2Tests
    {
        private static bool Loaded()
        {
            if (MapManager.GetMapInfo(91889155) == null) MapManager.Initialize();
            return MapManager.GetMapInfo(91889155) != null;
        }

        [Theory]
        [InlineData(68421638, 459526, 87821825, 383)]    // Madrestam: «Casa grande de Madrestam»
        [InlineData(68420611, 459524, 87819777, 397)]    // Madrestam: landed elsewhere than Jondo's
        [InlineData(87819779, 468062, 87821825, 317)]
        [InlineData(87821825, 468065, 87818755, 285)]
        [InlineData(91889153, 540476, 90708738, 257)]    // Sufokia's guild hall
        [InlineData(91889153, 540529, 91889155, 452)]
        [InlineData(95422722, 540956, 88087812, 306)]    // Sufokia's boat and rope
        [InlineData(95422722, 540957, 240386562, 383)]
        [InlineData(95420418, 488049, 148637187, 485)]   // the Eliotrope portal and back
        [InlineData(148637187, 488050, 95420418, 160)]
        [InlineData(183767052, 515203, 183767050, 505)]
        [InlineData(206307332, 538730, 207625216, 400)]  // Pandala
        [InlineData(173540352, 509500, 173934338, 413)]  // Saharach
        [InlineData(54176556, 538592, 54658069, 424)]    // Frigost
        [InlineData(185336832, 517108, 185337856, 452)]  // one doorway in two parts on cell 249:
        [InlineData(185336832, 517107, 185337856, 452)]  // the part not used lands as the one used
        public void The_door_lands_where_the_official_server_put_the_character(long map, int element, long toMap, int toCell)
        {
            if (!Loaded()) return;
            TeleportManager.Initialize();
            Assert.True(TeleportManager.TryGet(map, element, out var t));
            Assert.Equal((toMap, toCell), (t!.DestinationMapId, t.DestinationCellId));
        }

        /// <summary>Sufokia's abyssal lifts: each way lands where the official server put the character.</summary>
        [Theory]
        [InlineData(117703169, 483314, 240, 117704202, 372)]
        [InlineData(117704202, 483313, 212, 117703169, 454)]
        public void The_lifts_land_on_the_measured_cell(long map, int element, int skill, long toMap, int toCell)
        {
            if (!Loaded()) return;
            InteractiveWays.Forget();
            var way = InteractiveWays.Of(map, element)!.Ways.Single(w => w.Skill == skill);
            Assert.Equal((toMap, toCell), (way.ToMap, way.ToCell));
        }

        /// <summary>Sufokia's guild hall: 540333 is its zaap (type 16, skill 114, the zaap list), not a passage.</summary>
        [Fact]
        public void The_guild_hall_zaap_is_a_zaap()
        {
            if (!Loaded()) return;
            Interactives.Initialize();
            TeleportManager.Initialize();
            Assert.Contains(Interactives.ZaapElements(91889155), e => e.Id == 540333);
            Assert.False(TeleportManager.TryGet(91889155, 540333, out _));
        }
    }
}
