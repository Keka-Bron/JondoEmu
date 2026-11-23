using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Dungeons whose catalogue named the post-boss room «Salida» as the way out, so leaving it led back
    /// into it: the Cámara de los Maleficios (140) and the Bastión de los Martilloseco (141). Winning the
    /// boss room loads that room, and its NPC and the map panel take the winners out to the entrance map.
    /// </summary>
    [Collection("MapManager")]
    public sealed class PostBossExitRoomTests
    {
        [Theory]
        [InlineData(140, 223875072L, 223875074L, 224660233L, 6946, 73343L)]
        [InlineData(141, 225185792L, 225185794L, 225313792L, 6847, 69193L)]
        public void The_salida_room_comes_after_the_boss_and_leads_out_to_the_entrance(
            int dungeonId, long bossRoom, long exitRoom, long entrance, int npc, long reply)
        {
            DungeonManager.Initialize();
            DungeonExitRooms.Forget();

            var room = DungeonExitRooms.AfterBoss(bossRoom);
            Assert.NotNull(room);
            Assert.Equal(dungeonId, room!.Dungeon);
            Assert.Equal(exitRoom, room.MapId);
            Assert.Equal(npc, room.Npc);
            Assert.Contains(reply, room.Replies);
            Assert.Same(room, DungeonExitRooms.At(exitRoom));

            var dungeon = DungeonManager.Get(dungeonId);
            Assert.NotNull(dungeon);
            Assert.Equal(entrance, DungeonManager.WayOut(dungeon!));
            Assert.NotEqual(exitRoom, DungeonManager.WayOut(dungeon!));
            Assert.Contains(bossRoom, dungeon!.Rooms);
        }
    }
}
