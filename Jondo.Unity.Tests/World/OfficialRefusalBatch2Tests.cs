using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Passages the official server refused with its own message to a character not meeting what opens
    /// them (packet captures, 2026-11-20 to 11-24): the message, its type and the criterion on the row.
    /// </summary>
    [Collection("MapManager")]
    public sealed class OfficialRefusalBatch2Tests
    {
        [Theory]
        [InlineData(207623172, 520116, 1271, 0)]   // Pandala's archive room, staff only
        [InlineData(173016076, 511984, 525, 1)]    // Sidimote: «...sem a Égide de Feca...»
        [InlineData(103549440, 487849, 449, 1)]    // the holes for the shrunk
        [InlineData(192677888, 515321, 449, 1)]
        public void The_passage_refuses_with_the_official_message(long map, int element, int message, int type)
        {
            if (MapManager.GetMapInfo(map) == null) MapManager.Initialize();
            if (MapManager.GetMapInfo(map) == null) return;
            TeleportManager.Initialize();
            Assert.True(TeleportManager.TryGet(map, element, out var route));
            Assert.Equal((message, type), (route.Refusal, route.RefusalType));
            Assert.False(string.IsNullOrEmpty(route.Criterion));
        }

        /// <summary>
        /// The Sidimote gate has no criterion in the client's world graph. Holding the Égide de Feca
        /// (19390, quest 1938) is INFERRED from the refusal's words; the open case was not measured.
        /// </summary>
        [Fact]
        public void The_Sidimote_gate_asks_for_the_Egide_of_Feca()
        {
            if (MapManager.GetMapInfo(173016076) == null) MapManager.Initialize();
            if (MapManager.GetMapInfo(173016076) == null) return;
            TeleportManager.Initialize();
            Assert.True(TeleportManager.TryGet(173016076, 511984, out var gate));
            Assert.Equal("PO>19390,0", gate.Criterion);
        }
    }
}
