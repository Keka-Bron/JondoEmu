using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A dungeon room fights on its own twin arena, placed on that arena's own cells, the same every
    /// time. Measured on the Royal Gobball's five rooms against the real game's screenshots: four of
    /// them were sent to another room's twin -- its scenery and its placement --, and every fight
    /// was drawn anew.
    /// </summary>
    [Collection("MapManager")]
    public class DungeonPlacementTests
    {
        [Theory]
        [InlineData(121373185, 121373190)]   // room 1
        [InlineData(121374209, 121374214)]   // room 2
        [InlineData(121375233, 121375238)]   // room 3
        [InlineData(121373187, 121373192)]   // room 4
        [InlineData(121374211, 121374216)]   // room 5, the boss
        public void Each_Royal_Gobball_room_fights_on_its_own_twin(long room, long board)
        {
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            if (DungeonPlacements.BoardOf(room) == 0 || !MapManager.Maps.ContainsKey(board)) return;   // no data

            Assert.Equal(board, MapManager.ResolveArenaMapId(room));
        }

        /// <summary>Room 2: the players in the middle, the monsters by pairs at the four corners.</summary>
        [Fact]
        public void A_dungeon_room_is_placed_on_its_board_s_own_cells_every_time()
        {
            const long Room2 = 121374209, Board = 121374214;
            int[] players = { 288, 301, 302, 315, 317, 329, 330, 344 };
            int[] monsters = { 229, 235, 243, 248, 383, 388, 397, 402 };

            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            DungeonManager.Initialize();
            if (!DungeonManager.IsRoom(Room2) || DungeonPlacements.Of(Room2, Board) == null) return;

            for (int fight = 0; fight < 3; fight++)
            {
                var (attackers, defenders) = FightHandler.TakePlacement(Room2, Board);
                Assert.Equal(players, attackers);
                Assert.Equal(monsters, defenders);
            }
        }
    }
}
