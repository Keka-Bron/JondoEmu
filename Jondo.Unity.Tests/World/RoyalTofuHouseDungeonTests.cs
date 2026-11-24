using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Royal Tofu House (dungeon 50, Dofus pour les noobs «Donjon du Tofulailler Royal»): the Third
    /// Room holds no fight, only holes that drop to the Cellar, whose ladder climbs back; its door
    /// leads on to the Fourth Room. The client flags one placement cell per team there, so the room
    /// was never meant to be fought. Everything else about the dungeon is the catalogue's.
    /// </summary>
    [Collection("MapManager")]
    public sealed class RoyalTofuHouseDungeonTests
    {
        private const int Id = 50;
        private const long First = 96338948, Second = 96338950, Third = 96206854, Cellar = 96207876,
            Fourth = 96207878, Fifth = 96208902, Boss = 96209926, Entrance = 96338944, Exit = 10231814;
        private const int ThirdRoomDoor = 472876, CellarLadder = 472842;

        private static DungeonManager.Dungeon Load()
        {
            if (MapManager.GetMapInfo(First) == null) MapManager.Initialize();
            TeleportManager.Initialize();
            DungeonManager.Initialize();
            MobSpawnManager.EnsureMonsterData();
            return DungeonManager.Get(Id)!;
        }

        [Fact]
        public void The_third_room_and_the_cellar_are_walked_through_without_a_fight()
        {
            var royal = Load();
            Assert.True(royal.Authored);
            Assert.Equal(Third, DungeonManager.NextRoom(royal, Second));
            Assert.Equal(Fourth, DungeonManager.NextRoom(royal, Third));
            Assert.Contains(Third, royal.EmptyRooms);
            Assert.Contains(Cellar, royal.EmptyRooms);
            Assert.Equal(2, royal.EmptyRooms.Count);
            Assert.Null(MobSpawnManager.RecomposeDungeonRoom(Third));
            Assert.Null(MobSpawnManager.RecomposeDungeonRoom(Cellar));
            Assert.DoesNotContain(Fourth, royal.EmptyRooms);
            Assert.DoesNotContain(Fifth, royal.EmptyRooms);
            Assert.DoesNotContain(Boss, royal.EmptyRooms);
        }

        [Fact]
        public void The_rest_of_the_dungeon_is_what_the_catalogue_built()
        {
            var royal = Load();
            Assert.Equal("Tofullinero Real", royal.Name);
            Assert.Equal(110, royal.MinLevel);
            Assert.Equal(120, royal.OptimalLevel);
            Assert.Equal(2, royal.Difficulty);
            Assert.Equal(Entrance, royal.EntranceMapId);
            Assert.Equal(Exit, royal.ExitMapId);
            Assert.Equal(First, royal.FirstRoom);
            Assert.Equal(Boss, royal.LastRoom);
            Assert.Equal(new[] { 382 }, royal.Bosses);
            Assert.True(royal.OnKeyring);
            Assert.True(royal.AutoGroup);
            Assert.True(royal.KeepsADoor);
            Assert.False(royal.BossAlone);
            Assert.Equal(new[] { (8142, 1) }, royal.Required);
            Assert.Equal(new[] { First, Second, Third, Cellar, Fourth, Fifth, Boss }, royal.Rooms);
            Assert.Equal(Fifth, DungeonManager.NextRoom(royal, Fourth));
            Assert.Equal(Boss, DungeonManager.NextRoom(royal, Fifth));
            Assert.Equal(0, DungeonManager.NextRoom(royal, Boss));
            Assert.Equal(Id, DungeonManager.OfRoom(Boss)?.Id);
        }

        [Fact]
        public void The_third_room_door_leads_on_and_the_cellar_ladder_climbs_back()
        {
            Load();
            Assert.True(TeleportManager.TryGet(Third, ThirdRoomDoor, out var door));
            Assert.Equal(Fourth, door.DestinationMapId);
            Assert.True(TeleportManager.TryGet(Cellar, CellarLadder, out var ladder));
            Assert.Equal(Third, ladder.DestinationMapId);
        }
    }
}
