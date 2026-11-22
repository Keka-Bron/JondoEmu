using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Conversations as the official server holds them (packet captures, 2026-11-17 to 11-21): the
    /// lines and replies it offered a character on no quest, and what a reply did besides speaking,
    /// read with the capture's clock (the server's answer within the same second as the click).
    /// </summary>
    [Collection("MapManager")]
    public sealed class MeasuredDialogueTests
    {
        private static DialogueChoice? Reply(int npc, long map, long line, int reply)
            => NpcDialogues.For(npc, map)?.Line(line)?.Choice(reply);

        private static bool Loaded()
        {
            if (MapManager.GetMapInfo(99091969) == null) MapManager.Initialize();
            TeleportManager.Initialize();
            return NpcDialogues.For(127, 99091969) != null;
        }

        /// <summary>
        /// «Gritar o mais alto que puder.» and Ary Stotles throws you out of the library: the
        /// character lands on 88082198, cell 357. And the other replies that moved the character.
        /// </summary>
        [Theory]
        [InlineData(127, 99091969, 10326, 11274, 88082198, 357)]      // Ary Stotles
        [InlineData(6637, 17048580, 50204, 66957, 185862661, 415)]    // the offering box's kamas
        [InlineData(7913, 88213271, 60123, 82507, 240123904, 521)]    // the dragons' champion
        [InlineData(7893, 240123904, 60050, 82505, 88213271, 276)]    // Aerafal, back to Amakna
        [InlineData(1217, 82315009, 7546, 7464, 83100164, 415)]       // Norach, «Sim.»
        public void The_reply_moves_the_character_where_the_official_server_did(
            int npc, long map, long line, int reply, long toMap, int toCell)
        {
            Assert.True(Loaded());
            var choice = Reply(npc, map, line, reply);
            Assert.NotNull(choice);
            Assert.Equal(reply, choice!.TeleportPassage);
            Assert.True(TeleportManager.TryGetNpcPassage(map, reply, npc, out var route));
            Assert.Equal((toMap, toCell), (route.DestinationMapId, route.DestinationCellId));
        }

        [Fact]
        public void The_compass_replies_point_where_the_official_server_pointed()
        {
            Assert.True(Loaded());
            Assert.Equal(new[] { -3, 10, 4 }, Reply(6817, 68419587, 51588, 69204)!.Compass.ToArray());
            Assert.Equal(new[] { -24, -22, 4 }, Reply(2435, 88213271, 17922, 21485)!.Compass.ToArray());
        }

        [Fact]
        public void What_the_replies_charge_give_and_say()
        {
            Assert.True(Loaded());
            var grab = Reply(6637, 17048580, 50204, 66957)!;
            Assert.Equal((500L, 1434), (grab.BuysPrice, grab.Says));
            var donate = Reply(6637, 17048580, 50205, 66976)!;
            Assert.Equal((1000L, 1438), (donate.BuysPrice, donate.Says));
            Assert.Equal(10L, Reply(80, 99093001, 1345, 1004)!.BuysPrice);     // Karafey, «Entregar 10 kamas.»
            Assert.Equal(1655, Reply(178, 88080663, 682, 619)!.Gives);          // Don Ruan
        }

        [Theory]
        [InlineData(7529, 55880, 77732)]   // Fifi de Lificha, «Dar uma olhada no que ele tem a oferecer.»
        [InlineData(6831, 51321, 68886)]   // Tokageko, «Resolver dar uma olhada na loja desse mercenário.»
        public void The_shop_replies_open_the_shop(int npc, long line, int reply)
        {
            Assert.True(Loaded());
            Assert.True(NpcDialogues.For(npc, 0)?.Line(line)?.Choice(reply)?.OpensShop);
        }

        /// <summary>The openings the official server used for a character on no quest.</summary>
        [Theory]
        [InlineData(522, 48356)]   // Agaes Becê, the bankers' greeting
        [InlineData(503, 2190)]    // Enileda
        [InlineData(1577, 11439)]  // had no conversation written: it is the official one now
        public void The_conversation_opens_on_the_official_line(int npc, long opening)
        {
            Assert.True(Loaded());
            Assert.Equal(opening, NpcDialogues.For(npc, 0)?.Opening);
        }
    }
}
