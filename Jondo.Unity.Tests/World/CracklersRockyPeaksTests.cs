using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Crackler's Rocky Peaks (Dofus pour les noobs, «Pitons Rocheux des Craqueleurs»): five fights,
    /// then the Sixth Room's invisible bridge, walked on its middle line; its outer lines drop to the
    /// Plateau, whose fight brings the party back up to the bridge; the stairs on the far side climb
    /// to the boss. The catalogue put a fight in the bridge room whose win went straight to the boss.
    /// </summary>
    [Collection("MapManager")]
    public sealed class CracklersRockyPeaksTests
    {
        private const int Id = 18;
        private const long First = 106954752, Fourth = 106957824, Fifth = 106958848, Bridge = 106959872,
                           Plateau = 106959874, Walkway = 106957826, WalkwayEnd = 106957828, Boss = 106960896;
        private const int BridgeFoot = 372, Stairs = 480373;

        private static DungeonManager.Dungeon Load()
        {
            if (MapManager.GetMapInfo(First) == null) MapManager.Initialize();
            DungeonManager.Initialize();
            MobSpawnManager.EnsureMonsterData();
            return DungeonManager.Get(Id)!;
        }

        [Fact]
        public void The_fifth_win_leads_to_the_bridge_and_a_fall_back_up_to_it()
        {
            var peaks = Load();
            Assert.True(peaks.Authored);
            Assert.True(peaks.KeepsADoor);
            Assert.Equal(First, peaks.FirstRoom);
            Assert.Equal(Boss, peaks.LastRoom);
            Assert.Equal(Bridge, DungeonManager.NextRoom(peaks, Fifth));
            Assert.Equal(Id, DungeonManager.OfRoom(Plateau)!.Id);

            // The Plateau's win lands at the foot of the bridge, however many times it is won.
            var hub = peaks.Hub!;
            Assert.Equal((Bridge, BridgeFoot), hub.AfterAWin(Plateau, new long[0]));
            Assert.Equal((Bridge, BridgeFoot), hub.AfterAWin(Plateau, new[] { Plateau }));
            Assert.Null(hub.AfterAWin(Fifth, new long[0]));
            Assert.Null(hub.AfterAWin(Boss, new long[0]));
        }

        [Fact]
        public void The_bridge_and_the_walkway_are_empty_and_the_plateau_and_boss_room_are_not()
        {
            var peaks = Load();
            foreach (long empty in new[] { Bridge, Walkway, WalkwayEnd })
            {
                Assert.Contains(empty, peaks.EmptyRooms);
                Assert.Null(MobSpawnManager.RecomposeDungeonRoom(empty));
            }
            Assert.DoesNotContain(Fourth, peaks.EmptyRooms);
            Assert.DoesNotContain(Plateau, peaks.EmptyRooms);
            Assert.Contains(MobSpawnManager.RecomposeDungeonRoom(Boss)!.Members, m => m.Monster.Id == 669);
        }

        [Fact]
        public void The_outer_lines_drop_to_the_plateau_and_the_middle_one_holds()
        {
            Load();
            TeleportManager.Initialize();
            foreach (int cell in new[] { 357, 344, 330, 317, 303, 290, 276, 359, 346, 332, 319 })
            {
                Assert.True(TeleportManager.TryGetCellTrigger(Bridge, cell, out var fall), $"cell {cell}");
                Assert.Equal(Plateau, fall.DestinationMapId);
            }
            foreach (int cell in new[] { BridgeFoot, 358, 345, 331, 318, 304, 291, 277 })
                Assert.False(TeleportManager.TryGetCellTrigger(Bridge, cell, out _), $"cell {cell}");

            Assert.True(TeleportManager.TryGet(Bridge, Stairs, out var up));
            Assert.Equal(Boss, up.DestinationMapId);
            Assert.True(TeleportManager.TryGet(Walkway, 480371, out var ladder));
            Assert.Equal(Fourth, ladder.DestinationMapId);
        }
    }
}
