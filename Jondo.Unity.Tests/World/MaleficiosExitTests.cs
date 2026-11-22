using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Cámara de los Maleficios (dungeon 140): its catalogue named the post-boss room «Salida» as the
    /// way out, so leaving it led back into it. Winning the boss room loads that room, and its NPC and the
    /// map panel take the winners out to the entrance map.
    /// </summary>
    [Collection("MapManager")]
    public sealed class MaleficiosExitTests
    {
        private const int Dungeon = 140;
        private const long BossRoom = 223875072, ExitRoom = 223875074, Entrance = 224660233;

        [Fact]
        public void The_salida_room_comes_after_the_boss_and_leads_out_to_the_entrance()
        {
            DungeonManager.Initialize();
            DungeonExitRooms.Forget();

            var room = DungeonExitRooms.AfterBoss(BossRoom);
            Assert.NotNull(room);
            Assert.Equal(ExitRoom, room!.MapId);
            Assert.Equal(6946, room.Npc);
            Assert.Contains(73343L, room.Replies);
            Assert.Same(room, DungeonExitRooms.At(ExitRoom));

            var dungeon = DungeonManager.Get(Dungeon);
            Assert.NotNull(dungeon);
            Assert.Equal(Entrance, DungeonManager.WayOut(dungeon!));
            Assert.NotEqual(ExitRoom, DungeonManager.WayOut(dungeon!));
            Assert.Contains(BossRoom, dungeon!.Rooms);
        }
    }
}
