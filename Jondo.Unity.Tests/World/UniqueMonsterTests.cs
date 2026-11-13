using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Lord Crow's library holds one of each of its three unique monsters, in a group of its rooms:
    /// the groups read or drawn have them swapped for their ordinary version, and the one beaten
    /// stands again once its respawn time is over.
    /// </summary>
    [Collection("MapManager")]
    public class UniqueMonsterTests
    {
        private const int Zorio = 823, Zorrowapo = 820;
        private const long RoomTwo = 159122432, RoomThree = 159123456;

        private static UniqueMonsters.Unique Row(int minutes = 0) => new()
        {
            Monster = Zorio, Ordinary = Zorrowapo, Maps = new[] { RoomTwo, RoomThree }, RespawnMinutes = minutes,
        };

        [Fact]
        public void The_file_is_read_and_the_rows_without_a_map_or_an_ordinary_one_are_left_out()
        {
            var rows = UniqueMonsters.Parse("""
                { "uniques": [
                  { "monster": 823, "ordinary": 820, "respawnMinutes": 5, "maps": [159122432, 159122432, 159123456] },
                  { "monster": 824, "ordinary": 0, "maps": [159122432] },
                  { "monster": 825, "ordinary": 819, "maps": [] },
                  { "monster": 818, "ordinary": 818, "maps": [159122432] }
                ] }
                """);

            var row = Assert.Single(rows);
            Assert.Equal(Zorio, row.Monster);
            Assert.Equal(Zorrowapo, row.Ordinary);
            Assert.Equal(5, row.RespawnMinutes);
            Assert.Equal(new[] { RoomTwo, RoomThree }, row.Maps);
        }

        [Fact]
        public void A_group_read_or_drawn_has_its_uniques_swapped_for_their_ordinary_version()
        {
            Use(Row());
            try
            {
                // As the base ships them: up to four in one group.
                var ids = new List<int> { Zorio, 281, Zorio, Zorrowapo, Zorio };
                Assert.Equal(3, UniqueMonsters.Strip(ids));
                Assert.Equal(new[] { Zorrowapo, 281, Zorrowapo, Zorrowapo, Zorrowapo }, ids);
            }
            finally
            {
                Restore();
            }
        }

        [Fact]
        public void One_stands_at_a_time_and_the_beaten_one_comes_back_after_its_time()
        {
            var now = new DateTime(2026, 11, 13, 17, 0, 0, DateTimeKind.Utc);
            var clock = UniqueMonsters.Clock;
            UniqueMonsters.Clock = () => now;
            Use(Row(minutes: 30));
            try
            {
                bool there = true;
                Assert.Equal(Zorio, Assert.Single(UniqueMonsters.Due((_, _, _) => there)).Monster);

                UniqueMonsters.Placed(Zorio, RoomTwo, -1022392);
                Assert.Empty(UniqueMonsters.Due((_, _, _) => there));
                Assert.Equal((RoomTwo, -1022392L), UniqueMonsters.WhereIs(Zorio));

                // Another group of the same room going away does not let it go.
                UniqueMonsters.Released(RoomTwo, -1022391);
                Assert.Empty(UniqueMonsters.Due((_, _, _) => there));

                // Its own group beaten: away for its thirty minutes, then due again.
                UniqueMonsters.Released(RoomTwo, -1022392);
                Assert.Null(UniqueMonsters.WhereIs(Zorio));
                Assert.Empty(UniqueMonsters.Due((_, _, _) => there));
                now = now.AddMinutes(30);
                Assert.Single(UniqueMonsters.Due((_, _, _) => there));
            }
            finally
            {
                UniqueMonsters.Clock = clock;
                Restore();
            }
        }

        [Fact]
        public void One_whose_group_went_away_without_its_fight_stands_again_at_once()
        {
            Use(Row(minutes: 30));
            try
            {
                UniqueMonsters.Placed(Zorio, RoomThree, -1022405);

                // Emptied by hand, or the map put back as it was loaded: it stands nowhere.
                Assert.Single(UniqueMonsters.Due((_, _, _) => false));
                Assert.Null(UniqueMonsters.WhereIs(Zorio));
            }
            finally
            {
                Restore();
            }
        }

        [Fact]
        public void The_library_has_its_three_in_its_sixteen_rooms()
        {
            string path = Paths.ContentFile(UniqueMonsters.File);
            if (!File.Exists(path)) return;

            var rows = UniqueMonsters.Parse(File.ReadAllText(path)).ToDictionary(r => r.Monster);
            Assert.Equal(new[] { 823, 824, 825 }, rows.Keys.OrderBy(k => k));
            Assert.Equal(820, rows[823].Ordinary);   // Zorio, el Zorrowapo
            Assert.Equal(818, rows[824].Ordinary);   // Horacio, el Cuerbok Domesticado
            Assert.Equal(819, rows[825].Ordinary);   // Kondonio, el Bebedor
            Assert.All(rows.Values, row => Assert.Equal(16, row.Maps.Count));
        }

        private static void Use(params UniqueMonsters.Unique[] rows) => UniqueMonsters.Use(rows);

        /// <summary>Back to the content's rows, nobody placed, for whoever loads the world next.</summary>
        private static void Restore()
        {
            string path = Paths.ContentFile(UniqueMonsters.File);
            UniqueMonsters.Use(File.Exists(path) ? UniqueMonsters.Parse(File.ReadAllText(path)) : Array.Empty<UniqueMonsters.Unique>());
        }
    }
}
