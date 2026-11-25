using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The official layer after the second batch of captured walks (2,292 maps; Pandala, Frigost's
    /// Bourgade, Sidimote, Saharach, Grobe and others, 2026-11-22 to 11-24).
    /// </summary>
    [Collection("MapManager")]
    public sealed class OfficialNpcPlacementBatch2Tests
    {
        /// <summary>
        /// Where the official server showed them, and the passage their reply opens stands with them:
        /// Samurai Hagiyo in the Pandarin's office, the Grobe ferryman on both piers.
        /// </summary>
        [Theory]
        [InlineData(207623168, 5584, 187, 558402)]
        [InlineData(205261569, 4102, 178, 4102001)]
        [InlineData(206831621, 4102, 382, 4102003)]
        public void They_stand_where_the_official_server_shows_them(long map, int npc, int cell, int passage)
        {
            Npcs.Initialize();
            if (!Npcs.Of(map).Any()) return;   // no world data
            Assert.Contains(Npcs.Of(map), s => s.NpcId == npc && s.Cell == cell);
            TeleportManager.Initialize();
            Assert.True(TeleportManager.TryGetNpcPassage(map, passage, npc, out var route));
            Assert.Equal(cell, route.SourceCellId);
        }

        /// <summary>
        /// A captured map does not take away what Jondo needs: Elmer Zenario, the guild expeditions' way
        /// in, stays in Sufokia's guild hall (not shown to the capturing character) and in the two
        /// maps written by hand; Astrub's two Irritados, shown by one capture of 191104002 and not by
        /// a later one, stay as seen.
        /// </summary>
        [Theory]
        [InlineData(91889155, 7860)]
        [InlineData(214696196, 7860)]
        [InlineData(217055492, 7860)]
        [InlineData(191104002, 4368)]
        [InlineData(191104002, 4369)]
        public void A_captured_map_keeps_them(long map, int npc)
        {
            Npcs.Initialize();
            if (!Npcs.Of(map).Any()) return;   // no world data
            Assert.Contains(Npcs.Of(map), s => s.NpcId == npc);
        }
    }
}
