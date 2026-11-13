using System.IO;
using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The feathers of Lord Crow's library: each of its unique monsters leaves one when beaten in
    /// the library, and the win in the Master's throne room takes the three back.
    /// </summary>
    public class FightWinAlterationTests
    {
        [Fact]
        public void The_rows_without_a_monster_or_an_alteration_are_left_out()
        {
            var wins = FightWinAlterations.Parse("""
                { "wins": [
                  { "monster": 824, "subarea": 1038, "alteration": 632, "lostAt": 159125512 },
                  { "monster": 0, "alteration": 633 },
                  { "monster": 825, "alteration": 0 }
                ] }
                """);

            var win = Assert.Single(wins);
            Assert.Equal(824, win.Monster);
            Assert.Equal(1038, win.SubArea);
            Assert.Equal(632, win.Alteration);
            Assert.Equal(159125512, win.LostAt);
        }

        [Fact]
        public void A_unique_beaten_in_the_library_leaves_its_feather_and_nowhere_else()
        {
            if (!File.Exists(Paths.ContentFile(FightWinAlterations.File))) return;

            Assert.Equal(new[] { 632 }, FightWinAlterations.Gained(new[] { 824, 818, 281 }, 1038));
            Assert.Equal(new[] { 632, 633, 634 }, FightWinAlterations.Gained(new[] { 823, 824, 825, 823 }, 1038).OrderBy(a => a));

            // Beside the Master in his throne room (subarea 211) they leave nothing.
            Assert.Empty(FightWinAlterations.Gained(new[] { 823, 824, 825 }, 211));
            Assert.Empty(FightWinAlterations.Gained(new[] { 818, 819, 820, 281 }, 1038));
        }

        [Fact]
        public void The_win_in_the_throne_room_takes_the_three_back()
        {
            if (!File.Exists(Paths.ContentFile(FightWinAlterations.File))) return;

            Assert.Equal(new[] { 632, 633, 634 }, FightWinAlterations.Lost(159125512).OrderBy(a => a));
            Assert.Empty(FightWinAlterations.Lost(159121666));
            Assert.Empty(FightWinAlterations.Lost(0));
        }
    }
}
