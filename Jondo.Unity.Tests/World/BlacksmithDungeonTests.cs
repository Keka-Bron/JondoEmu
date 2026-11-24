using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Blacksmith Dungeon (Dofus pour les noobs, «Donjon des Forgerons»): the Entrance leads on
    /// to the First Room, the Third, Sixth and Eighth Rooms are only walked through, the Fourth and
    /// Fifth Rooms are the short way to the Last, the blacksmith's, and the Emote Room's door opens
    /// on the Ninth, the long way's last fight. The catalogue walked twelve rooms in a row, fought
    /// the walk-through rooms on another room's board, and led the Fifth's winners to the Sixth.
    /// </summary>
    [Collection("MapManager")]
    public sealed class BlacksmithDungeonTests
    {
        private const int Id = 21, Blacksmith = 252;
        private const long Entrance = 87294465, First = 87295489, Third = 87297537, Fifth = 87295491, Sixth = 87294469,
                           Seventh = 87295493, Eighth = 87296517, Emote = 87297541, Ninth = 87294471,
                           Last = 87296515, SeventhBoard = 87299595;
        private const int EntranceDoor = 505079;

        private static DungeonManager.Dungeon Load()
        {
            if (MapManager.GetMapInfo(First) == null) MapManager.Initialize();
            DungeonManager.Initialize();
            return DungeonManager.Get(Id)!;
        }

        [Fact]
        public void The_fifth_and_ninth_rooms_lead_to_the_blacksmith_and_the_walk_through_rooms_hold_no_fight()
        {
            var forge = Load();
            Assert.True(forge.Authored);
            Assert.True(forge.KeepsADoor);
            Assert.Equal(Last, forge.LastRoom);
            Assert.Contains(Blacksmith, forge.Bosses);
            Assert.Equal(Last, DungeonManager.NextRoom(forge, Fifth));
            Assert.Equal(Last, DungeonManager.NextRoom(forge, Ninth));
            foreach (long room in new[] { Entrance, Third, Sixth, Eighth, Emote })
                Assert.Contains(room, forge.EmptyRooms);
            Assert.DoesNotContain(First, forge.EmptyRooms);
            Assert.DoesNotContain(Ninth, forge.EmptyRooms);
        }

        [Fact]
        public void The_entrance_door_opens_on_the_first_room()
        {
            Load();
            TeleportManager.Initialize();
            Assert.True(TeleportManager.TryGet(Entrance, EntranceDoor, out var door));
            Assert.Equal(First, door.DestinationMapId);
        }

        [Fact]
        public void The_seventh_room_fights_on_its_own_arena()
        {
            Assert.Equal(SeventhBoard, DungeonPlacements.BoardOf(Seventh));
            Assert.NotNull(DungeonPlacements.Of(Seventh, SeventhBoard));
        }
    }
}
