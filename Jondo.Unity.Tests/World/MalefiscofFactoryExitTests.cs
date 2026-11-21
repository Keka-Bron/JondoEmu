using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Fábrica de Malefíscof: the win in the machine room leaves the winners in the factory's
    /// exit room, where «El maleficio del cofre» (1506) has them talk to Malefíscof, and Inès Karcel
    /// takes them out with «Salir.». The room is not one of dungeon 77's maps, so the client shows
    /// no map panel exit there: without her, nobody left it.
    /// </summary>
    [Collection("MapManager")]
    public sealed class MalefiscofFactoryExitTests
    {
        private const long Hall = 130285568, MachineRoom = 130286592, ExitRoom = 130287616, Outside = 129368578;
        private const int Ines = 2353, Malefiscof = 2395, Leave = 21430;

        private static void Load()
        {
            if (MapManager.GetMapInfo(ExitRoom) == null) MapManager.Initialize();
            DungeonManager.Initialize();
        }

        [Fact]
        public void Winning_the_machine_room_leads_to_the_exit_room()
        {
            Load();
            if (DungeonManager.OfRoom(MachineRoom) == null) return;   // no data

            var (map, cell) = DungeonHandler.AfterAWinIn(MachineRoom);
            Assert.Equal(ExitRoom, map);
            Assert.Equal(426, cell);
            Assert.False(DungeonManager.IsRoom(ExitRoom));
        }

        [Fact]
        public void Ines_leave_reply_in_the_exit_room_goes_outside()
        {
            Load();
            if (DungeonManager.Get(77) == null) return;   // no data

            Assert.Equal(Outside, DungeonHandler.ExitForReply(Ines, ExitRoom, Leave));
            Assert.Equal(0, DungeonHandler.ExitForReply(Ines, Hall, Leave));
            Assert.Equal(0, DungeonHandler.ExitForReply(Malefiscof, ExitRoom, Leave));
        }

        [Fact]
        public void Ines_and_Malefiscof_wait_in_the_exit_room()
        {
            Load();
            Npcs.Initialize();
            if (Npcs.TemplateOf(Ines) == null) return;   // no data

            Assert.Contains(Npcs.OnMap(ExitRoom), n => n.NpcId == Ines);
            Assert.Contains(Npcs.OnMap(ExitRoom), n => n.NpcId == Malefiscof);
        }

        /// <summary>
        /// The machine room's group stands on its floor: the base seeded it on 350, a wall cell
        /// outside the room, and a boss room's group is never spread by the base's own placing.
        /// </summary>
        [Fact]
        public void The_machine_rooms_group_stands_on_its_floor()
        {
            Load();
            if (!MapManager.WalkableCells.ContainsKey(MachineRoom)) return;   // no data

            int cell = MobSpawnManager.MiddleOfTheFloor(MachineRoom);
            Assert.True(MapManager.IsCellWalkable(MachineRoom, cell));
            Assert.NotEqual(350, cell);
        }
    }
}
