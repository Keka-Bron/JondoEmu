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

        /// <summary>
        /// The Royal Mastogob's Greenhouse: each room on the arena called as it is, and the sixth,
        /// the boss's, on the one arena drawn with its dome, sunflowers and orange tree. Matched by
        /// the walkable cells, the first, fourth and sixth rooms were all sent to the third room's
        /// board, and the boss stood among its walkways, with no way to anybody.
        /// </summary>
        [Theory]
        [InlineData(55050240, 55050246)]   // first room
        [InlineData(55050242, 55051270)]   // second room
        [InlineData(55052290, 55052294)]   // third room
        [InlineData(55052288, 55053318)]   // fourth room
        [InlineData(55053312, 55054342)]   // sixth room, the boss
        public void Each_Royal_Mastogob_room_fights_on_its_own_board(long room, long board)
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

        /// <summary>
        /// Missiz Freezz's Frostforge (Frigost III on) puts each room's board on the room's own world
        /// cell: a twin with the room's very name, its id six past the room's. Left off the file,
        /// the first room was fought on itself, its fight drawn on the map's edge among the scenery.
        /// </summary>
        [Theory]
        [InlineData(109838849, 109838855)]   // first room
        [InlineData(109839873, 109839879)]   // second room
        [InlineData(109840897, 109840903)]   // third room
        [InlineData(109841921, 109841927)]   // fourth room
        public void Each_Missiz_Freezz_room_fights_on_its_namesake_twin(long room, long board)
        {
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            if (!MapManager.Maps.ContainsKey(board)) return;   // no data

            Assert.Equal(board, DungeonPlacements.BoardOf(room));
            Assert.Equal(board, MapManager.ResolveArenaMapId(room));
        }

        /// <summary>
        /// The Count's rooms are fought on the map six past each, whatever the maps are called: the
        /// Clockmaking Workshop's twin is named the Rhythmetick Room, and it was handed to that room
        /// by its name, so the workshop was fought on itself among its clocks and shelves.
        /// </summary>
        [Theory]
        [InlineData(112199169, 112199175)]   // Banquet Hall
        [InlineData(112200193, 112200199)]   // Glacial Gallery
        [InlineData(112201217, 112201223)]   // Clockmaking Workshop
        [InlineData(112198145, 112198151)]   // Sinistro Hall
        public void Each_Count_room_fights_on_the_twin_six_past_it(long room, long board)
        {
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            if (!MapManager.Maps.ContainsKey(board)) return;   // no data

            Assert.Equal(board, DungeonPlacements.BoardOf(room));
            Assert.Equal(board, MapManager.ResolveArenaMapId(room));
            Assert.NotEqual(112201223, DungeonPlacements.BoardOf(112202241));
        }

        /// <summary>
        /// A room with no board of its own -- Missiz Freezz's, the boss's -- is fought on itself, on
        /// the cells it flags, the same every time, never drawn at random over its scenery.
        /// </summary>
        [Fact]
        public void A_room_without_a_board_is_placed_on_its_own_flagged_cells()
        {
            const long BossRoom = 109840899;
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            DungeonManager.Initialize();
            if (!DungeonManager.IsRoom(BossRoom) || MapPlacements.Of(BossRoom) is not { } own) return;
            Assert.Equal(BossRoom, MapManager.ResolveArenaMapId(BossRoom));

            for (int fight = 0; fight < 3; fight++)
            {
                var (attackers, defenders) = FightHandler.TakePlacement(BossRoom, BossRoom);
                Assert.Equal(own.Blue, attackers);
                Assert.Equal(own.Red, defenders);
            }
        }
    }
}
