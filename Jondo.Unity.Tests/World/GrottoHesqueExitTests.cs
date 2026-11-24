using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Grotto Hesque (25): past the Coralator Magistrate's room lies the Grotto's exit, where
    /// Tina Montini waits to show the winners the way out. Winning the boss room dropped them at
    /// the dungeon's entrance instead.
    /// </summary>
    [Collection("MapManager")]
    public sealed class GrottoHesqueExitTests
    {
        private const int Id = 25, Tina = 941;
        private const long BossRoom = 5242886, ExitRoom = 5244422;
        private const long Greeting = 4191, Leave = 3669;

        [Fact]
        public void The_boss_win_leads_to_the_exit_room_and_tina_leads_out()
        {
            DungeonManager.Initialize();
            DungeonExitRooms.Forget();

            var room = DungeonExitRooms.AfterBoss(BossRoom);
            Assert.NotNull(room);
            Assert.Equal(Id, room!.Dungeon);
            Assert.Equal(ExitRoom, room.MapId);
            Assert.Equal(Tina, room.Npc);
            Assert.Contains(Leave, room.Replies);

            var grotto = DungeonManager.Get(Id)!;
            Assert.NotEqual(ExitRoom, DungeonManager.WayOut(grotto));

            var tree = NpcDialogues.For(Tina, ExitRoom);
            Assert.NotNull(tree);
            Assert.Equal(Greeting, tree!.Opening);
            Assert.True(tree.Offers(Leave));
        }
    }
}
