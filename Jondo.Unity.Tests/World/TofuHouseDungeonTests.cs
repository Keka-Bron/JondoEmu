using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Tofu House (dungeon 48, Dofus pour les noobs «Donjon des Tofus»): the Second Room holds no
    /// fight, only holes that drop to the Basement, whose ladder climbs back; its door leads on to the
    /// Third Room. The client flags one placement cell per team there, so the room was never meant
    /// to be fought, and the Basement was listed but never reached.
    /// </summary>
    [Collection("MapManager")]
    public sealed class TofuHouseDungeonTests
    {
        private const int Id = 48;
        private const long First = 96338946, Second = 96206850, Basement = 96206852, Third = 96206848, Boss = 96209922;
        private const int SecondRoomDoor = 472875, BasementLadder = 472840;

        private static DungeonManager.Dungeon Load()
        {
            if (MapManager.GetMapInfo(First) == null) MapManager.Initialize();
            DungeonManager.Initialize();
            MobSpawnManager.EnsureMonsterData();
            return DungeonManager.Get(Id)!;
        }

        [Fact]
        public void The_second_room_and_the_basement_are_walked_through_without_a_fight()
        {
            var tofus = Load();
            Assert.True(tofus.Authored);
            Assert.Equal(First, tofus.FirstRoom);
            Assert.Equal(Boss, tofus.LastRoom);
            Assert.Equal(Second, DungeonManager.NextRoom(tofus, First));
            Assert.Contains(Second, tofus.EmptyRooms);
            Assert.Contains(Basement, tofus.EmptyRooms);
            Assert.Null(MobSpawnManager.RecomposeDungeonRoom(Second));
            Assert.Null(MobSpawnManager.RecomposeDungeonRoom(Basement));
            Assert.DoesNotContain(Third, tofus.EmptyRooms);
        }

        [Fact]
        public void The_second_room_door_leads_on_and_the_basement_ladder_climbs_back()
        {
            Load();
            Assert.True(TeleportManager.TryGet(Second, SecondRoomDoor, out var door));
            Assert.Equal(Third, door.DestinationMapId);
            Assert.True(TeleportManager.TryGet(Basement, BasementLadder, out var ladder));
            Assert.Equal(Second, ladder.DestinationMapId);
        }
    }
}
