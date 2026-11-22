using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// A gatherable element is the resource the official server declares for it, not only what its
    /// drawing says. Every freshwater fishing spot is drawing 1018, which recursos lists as Gobião
    /// (75/124, level 1, «discrepa»); on the official server each spot is its own fish. Packet captures
    /// of the map contents (2026-11) give the type and skill per element; the job, item and level are
    /// the client's for that skill.
    /// </summary>
    [Collection("MapManager")]
    public sealed class ResourceByElementTests
    {
        private static void Load()
        {
            if (MapManager.GetMapInfo(189792259) == null) MapManager.Initialize();
            Assert.NotNull(MapManager.GetMapInfo(189792259));
            Interactives.Initialize();
            TeleportManager.Initialize();
            Resources.Initialize();
        }

        [Theory]
        [InlineData(189792259, 515603, 74, 125, 36, 1844, 20)]   // Truta (drawing 1018)
        [InlineData(189792771, 515604, 76, 126, 36, 603, 40)]    // Peixe-gatinho (drawing 1018)
        [InlineData(190055424, 515947, 71, 128, 36, 598, 10)]    // Gramarão (drawing 1018)
        [InlineData(101715467, 494535, 79, 127, 36, 1794, 60)]   // Carpa Diem (drawing 1018)
        [InlineData(193332225, 515759, 53, 25, 24, 441, 20)]     // Cobre: no resource at all before
        public void The_resource_is_the_one_the_official_server_declares(
            long map, int element, int type, int skill, int job, int item, int level)
        {
            Load();
            Assert.True(Resources.TryGet(map, element, out var r));
            Assert.Equal((type, skill, job, item, level), (r.Type, r.SkillId, r.JobId, r.ItemId, r.LevelMin));
        }
    }
}
