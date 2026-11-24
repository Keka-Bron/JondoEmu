using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Dungeons whose catalogue named the post-boss room «Salida» as the way out, so leaving it led back
    /// into it, or shut the winners in with no border, interactive or NPC: the Cámara de los Maleficios
    /// (140), the Bastión de los Martilloseco (141), the White and Black Rats' dungeons (42, 43),
    /// Kharnotaurus's Lair (115), the Sylvan Refuge (142) and Kickroach's Lair (88). Winning the boss room loads that room, and
    /// its NPC and the map panel take the winners out to the entrance map.
    /// </summary>
    [Collection("MapManager")]
    public sealed class PostBossExitRoomTests
    {
        [Theory]
        [InlineData(140, 223875072L, 223875074L, 224660233L, 6946, 73343L)]
        [InlineData(141, 225185792L, 225185794L, 225313792L, 6847, 69193L)]
        [InlineData(42, 218632192L, 218632194L, 216402698L, 6612, 66726L)]
        [InlineData(43, 218894336L, 218894338L, 216927751L, 6614, 66779L)]
        [InlineData(115, 199495680L, 199495682L, 84411905L, 4975, 47164L)]
        [InlineData(142, 224137216L, 224137218L, 223350028L, 6807, 69096L)]
        [InlineData(88, 146670592L, 146538496L, 103549440L, 2767, 23569L)]
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

        [Theory]
        [InlineData(4975, 199495682L, 35690L, 47164L)]
        [InlineData(6807, 224137218L, 51493L, 69096L)]
        [InlineData(2767, 146538496L, 19687L, 23569L)]
        public void The_exit_room_npc_greets_the_winners_and_offers_the_way_out(int npc, long room, long greeting, long leave)
        {
            var tree = NpcDialogues.For(npc, room);
            Assert.NotNull(tree);
            Assert.Equal(greeting, tree!.Opening);
            Assert.True(tree.Offers(leave));
            Assert.Null(NpcDialogues.For(npc, room + 1));
        }
    }
}
