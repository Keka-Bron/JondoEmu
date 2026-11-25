using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Gatherable elements as the official server declared them on the maps the captures entered
    /// (2026-11-20 to 11-24): its type and skill, with the client's job, item and level for that skill.
    /// </summary>
    [Collection("MapManager")]
    public sealed class MeasuredResourceBatch2Tests
    {
        [Theory]
        [InlineData(173936388, 509339, 25, 30, 313, 160)]
        [InlineData(173939460, 509342, 26, 31, 446, 140)]
        [InlineData(178782208, 510231, 37, 28, 443, 60)]
        public void The_resource_is_the_one_declared(long map, int element, int type, int skill, int item, int level)
        {
            if (MapManager.GetMapInfo(map) == null) MapManager.Initialize();
            if (MapManager.GetMapInfo(map) == null) return;
            Interactives.Initialize();
            Resources.Initialize();
            Assert.True(Resources.TryGet(map, element, out var r));
            Assert.Equal((type, skill, item, level), (r.Type, r.SkillId, r.ItemId, r.LevelMin));
        }

        /// <summary>
        /// Drawings the official server offered a gathering on, wherever they stand: 1110 «Recolher» (42),
        /// potatoes (537), gathered in Bonta; 1081 and 4918 «Coletar» (24), iron (312).
        /// </summary>
        [Theory]
        [InlineData(93849600, 480595, 42, 537)]
        [InlineData(30670857, 495388, 24, 312)]
        public void The_drawing_is_gathered_as_measured(long map, int element, int skill, int item)
        {
            if (MapManager.GetMapInfo(map) == null) MapManager.Initialize();
            if (MapManager.GetMapInfo(map) == null) return;
            Interactives.Initialize();
            Resources.Initialize();
            Assert.True(Resources.TryGet(map, element, out var r));
            Assert.Equal((skill, item), (r.SkillId, r.ItemId));
        }
    }
}
