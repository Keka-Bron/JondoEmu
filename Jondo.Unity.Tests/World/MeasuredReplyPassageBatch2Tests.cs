using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Replies that moved the character on the official server (packet captures, 2026-11-22 to 11-24):
    /// the passage they open leads where the character landed, from the map of the conversation.
    /// </summary>
    [Collection("MapManager")]
    public sealed class MeasuredReplyPassageBatch2Tests
    {
        private static bool Loaded()
        {
            if (MapManager.GetMapInfo(178784260) == null) MapManager.Initialize();
            TeleportManager.Initialize();
            NpcDialogues.Load();
            return MapManager.GetMapInfo(178784260) != null;
        }

        /// <summary>
        /// Feldz Paf's mine carts: «Subir a bordo do carrinho.» for 50 kamas, and again once the Shadow
        /// Miners are beaten; Ingradadu Dicerva's way out.
        /// </summary>
        [Theory]
        [InlineData(830, 178784260, 3454, 3049, 97255965, 304, 50)]
        [InlineData(830, 97261085, 3535, 3113, 97256975, 290, 0)]
        [InlineData(5567, 205522436, 41613, 54178, 205520902, 266, 0)]
        public void The_reply_leads_where_the_official_server_did(
            int npc, long map, long line, int reply, long toMap, int toCell, long price)
        {
            if (!Loaded()) return;
            var choice = NpcDialogues.For(npc, map)?.Line(line)?.Choice(reply);
            Assert.NotNull(choice);
            Assert.Equal(reply, choice!.TeleportPassage);
            Assert.Equal(price, choice.BuysPrice);
            Assert.True(TeleportManager.TryGetNpcPassage(map, reply, npc, out var route));
            Assert.Equal((toMap, toCell), (route.DestinationMapId, route.DestinationCellId));
        }

        /// <summary>The Grobe ferryman's way back to Pandala lands on 149, as measured, not on 163.</summary>
        [Fact]
        public void Back_from_Grobe_lands_where_the_official_server_put_the_character()
        {
            if (!Loaded()) return;
            Assert.True(TeleportManager.TryGetNpcPassage(206831621, 4102003, 4102, out var route));
            Assert.Equal((205261569L, 149), (route.DestinationMapId, route.DestinationCellId));
        }
    }
}
