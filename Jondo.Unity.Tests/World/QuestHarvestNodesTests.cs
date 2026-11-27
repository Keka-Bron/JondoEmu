using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Malt (405), maize (16454) and Lard Bass (1779), which the farmer and fisher quests ask for,
    /// had no gatherable node anywhere. Their drawings come from DofusDB's harvest maps matched to
    /// the client's map dump: Malt is drawing 667 on all of its maps, maize the three growth drawings
    /// 3227-3229 (as rice), and Lard Bass shares the sea-fishing drawing 1839 with other fish, so it
    /// is declared per element only where the spots on a map are as many as DofusDB counts and no
    /// other fish of that drawing is listed there. Type, skill, job and level are the client's for
    /// the skill (skills.json); the regrowth is every node's.
    /// </summary>
    [Collection("MapManager")]
    public sealed class QuestHarvestNodesTests
    {
        private static void Load()
        {
            if (MapManager.GetMapInfo(189792259) == null) MapManager.Initialize();
            Interactives.Initialize();
            TeleportManager.Initialize();
            Resources.Initialize();
        }

        [Theory]
        [InlineData(142088202, 500837, 47, 58, 28, 405, 120)]      // Malt, drawing 667
        [InlineData(142088202, 500840, 47, 58, 28, 405, 120)]      // Malt, drawing 667
        [InlineData(159236, 490039, 260, 307, 28, 16454, 160)]     // maize, drawing 3227
        [InlineData(159236, 490043, 260, 307, 28, 16454, 160)]     // maize, drawing 3228
        [InlineData(159236, 490040, 260, 307, 28, 16454, 160)]     // maize, drawing 3229
        [InlineData(181145610, 510362, 271, 325, 36, 1779, 160)]   // Lard Bass on drawing 1839
        [InlineData(84411398, 501445, 271, 325, 36, 1779, 160)]    // Lard Bass on drawing 1839
        [InlineData(168560650, 508382, 136, 345, 1, 18087, 1)]     // a shell of quest 1758, drawing 36326
        [InlineData(168559624, 508375, 136, 345, 1, 18087, 1)]     // a shell of quest 1758, drawing 36326
        [InlineData(54167895, 458488, 132, 189, 36, 11106, 200)]   // Icefish on drawing 1839
        [InlineData(54176040, 470180, 132, 189, 36, 11106, 200)]   // Icefish on drawing 1839
        public void The_quest_resources_can_be_gathered_where_the_game_has_them(
            long map, int element, int type, int skill, int job, int item, int level)
        {
            Load();
            Assert.True(Resources.TryGet(map, element, out var r));
            Assert.Equal((type, skill, job, item, level), (r.Type, r.SkillId, r.JobId, r.ItemId, r.LevelMin));
            Assert.Equal(System.TimeSpan.FromMinutes(5), Resources.Regrowth);
        }

        /// <summary>A sea-fishing spot that is not Lard Bass keeps the drawing's own fish.</summary>
        [Fact]
        public void Other_spots_of_the_sea_drawing_keep_their_fish()
        {
            Load();
            var spot = Interactives.ElementByGfx(84411397, 1839);
            Assert.NotEqual(0, spot.Id);
            Assert.True(Resources.TryGet(84411397, spot.Id, out var r));
            Assert.NotEqual(1779, r.ItemId);
        }
    }
}
