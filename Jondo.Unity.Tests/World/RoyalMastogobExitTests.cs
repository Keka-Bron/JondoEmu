using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Royal Mastogob's Greenhouse: the win over the Jalamut Real leaves the winners in the
    /// greenhouse's exit room, where «Le pouvoir derrière le trône» (1529) has them talk to Gavroch,
    /// and Topordo Spesos, the host who let them in, takes them out. The room has no door, and the
    /// client shows it no map panel exit: without him, nobody left it.
    /// </summary>
    [Collection("MapManager")]
    public sealed class RoyalMastogobExitTests
    {
        private const long BossRoom = 55053312, ExitRoom = 55054336, Outside = 54175024;
        private const int Topordo = 1285;

        private static void Load()
        {
            if (MapManager.GetMapInfo(ExitRoom) == null) MapManager.Initialize();
            DungeonManager.Initialize();
        }

        [Fact]
        public void Winning_the_boss_room_leads_to_the_exit_room()
        {
            Load();
            if (DungeonManager.OfRoom(BossRoom) == null) return;   // no data

            var (map, cell) = DungeonHandler.AfterAWinIn(BossRoom);
            Assert.Equal(ExitRoom, map);
            Assert.Equal(440, cell);
            Assert.False(DungeonManager.IsRoom(ExitRoom));
        }

        [Theory]
        [InlineData(7712)]
        [InlineData(7713)]
        [InlineData(7714)]
        public void Topordos_leave_replies_in_the_exit_room_go_outside(long reply)
        {
            Load();
            if (DungeonManager.Get(54) == null) return;   // no data

            Assert.Equal(Outside, DungeonHandler.ExitForReply(Topordo, ExitRoom, reply));
            Assert.Equal(0, DungeonHandler.ExitForReply(Topordo, BossRoom, reply));
            Assert.Equal(0, DungeonHandler.ExitForReply(2466, ExitRoom, reply));   // Gavroch
        }

        [Fact]
        public void Topordo_waits_in_the_exit_room()
        {
            Load();
            Npcs.Initialize();
            if (Npcs.TemplateOf(Topordo) == null) return;   // no data

            Assert.Contains(Npcs.OnMap(ExitRoom), n => n.NpcId == Topordo);
        }
    }
}
