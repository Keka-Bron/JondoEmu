using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Room order and boss-room choice: hubs and Exits must not steal LastRoom.
    /// </summary>
    /// <remarks>
    /// Sandy Castle (and Crackler's, Magik Riktus, …) used to keep the extractor's list because
    /// one map had no "Nth Room" ordinal, so ByName aborted and First Room sat at the end as the
    /// fake boss. Quest 896 then asked for the Fifth Room and never ticked.
    /// </remarks>
    public class DungeonRoomOrderTests
    {
        [Fact]
        public void ByName_orders_numbered_rooms_and_keeps_the_hub_after()
        {
            var names = new Dictionary<long, string>
            {
                [1] = "Sandy Castle - Second Room",
                [2] = "Sandy Castle - Third Room",
                [3] = "Sandy Castle - Fourth Room",
                [4] = "Sandy Castle - Fifth Room",
                [5] = "Sandy Castle",
                [6] = "Sandy Castle - First Room",
            };
            var rooms = new List<long> { 1, 2, 3, 4, 5, 6 };

            var ordered = DungeonManager.ByName(rooms, names);

            Assert.NotNull(ordered.Rooms);
            Assert.Equal(new long[] { 6, 1, 2, 3, 4, 5 }, ordered.Rooms);
            Assert.Equal(4, ordered.NumberedLast);
            Assert.Equal(4, DungeonManager.PickBossRoom(ordered.Rooms!, names, ordered.NumberedLast));
        }

        [Fact]
        public void ByName_prefers_the_prefix_that_owns_an_Exit()
        {
            // Koolich: Lair 1..6 and Cavern 1..5 + Exit. The cavern is the real walk.
            var names = new Dictionary<long, string>
            {
                [10] = "Koolich's Lair - First Room",
                [11] = "Koolich's Lair - Second Room",
                [20] = "Koolich Cavern - First Room",
                [21] = "Koolich Cavern - Second Room",
                [22] = "Koolich Cavern - Fifth Room",
                [29] = "Koolich Cavern - Exit",
            };
            var rooms = new List<long> { 10, 11, 20, 21, 22, 29 };

            var ordered = DungeonManager.ByName(rooms, names);

            Assert.NotNull(ordered.Rooms);
            Assert.Equal(new long[] { 20, 21, 22 }, ordered.Rooms!.Take(3));
            Assert.Equal(22, ordered.NumberedLast);
            Assert.Contains(29L, ordered.Rooms);
            Assert.Equal(22, DungeonManager.PickBossRoom(ordered.Rooms, names, ordered.NumberedLast));
        }

        [Fact]
        public void ByName_reads_rooms_numbered_with_figures_and_keeps_the_backstage_apart()
        {
            // Dramak's Theatre, in the catalogue's order: the Puppet Master's auditorium first, and
            // two runs numbered from 1. Walked: auditoriums 1 to 5, Dramak in the fourth and the
            // Puppet Master in the fifth; the backstage is the secret passage off the third.
            var names = new Dictionary<long, string>
            {
                [118231040] = "Dramak's Theatre - Auditorium 5",
                [118229250] = "Dramak's Theatre - Backstage 1",
                [118229252] = "Dramak's Theatre - Backstage 2",
                [118226944] = "Dramak's Theatre - Auditorium 1",
                [118227968] = "Dramak's Theatre - Auditorium 2",
                [118228992] = "Dramak's Theatre - Auditorium 3",
                [118230016] = "Dramak's Theatre - Auditorium 4",
            };
            var rooms = names.Keys.ToList();

            Assert.Null(DungeonManager.ByEveryName(rooms, names));   // two rooms 1, two rooms 2
            var ordered = DungeonManager.ByName(rooms, names);

            Assert.Equal(new long[] { 118226944, 118227968, 118228992, 118230016, 118231040, 118229250, 118229252 },
                         ordered.Rooms);
            Assert.Equal(118231040, DungeonManager.PickBossRoom(ordered.Rooms!, names, ordered.NumberedLast));
        }

        [Theory]
        [InlineData("Dramak's Theatre - Auditorium 5", 5)]
        [InlineData("Sandy Castle - Fifth Room", 5)]
        [InlineData("Dramak's Theatre - Exit", 0)]
        [InlineData("Sandy Castle", 0)]
        [InlineData("Brakmar 2", 0)]
        public void OrdinalOf_reads_words_and_figures(string name, int expected)
            => Assert.Equal(expected, DungeonManager.OrdinalOf(name));

        [Fact]
        public void PickBossRoom_skips_Exit_and_picks_the_throne()
        {
            var names = new Dictionary<long, string>
            {
                [1] = "Lord Crow's Library - Throne Room",
                [2] = "Lord Crow's Library - Antechamber",
                [3] = "Lord Crow's Library - Library Annexe",
            };

            Assert.Equal(1, DungeonManager.PickBossRoom(new long[] { 1, 2, 3 }, names));
        }

        [Fact]
        public void PickBossRoom_prefers_a_named_fissure_over_the_last_ordinal()
        {
            var names = new Dictionary<long, string>
            {
                [1] = "Ilyzaelle's Lookout - First Room",
                [2] = "Ilyzaelle's Lookout - Second Room",
                [3] = "Ilyzaelle's Lookout - Erzal's Fissure",
            };

            Assert.Equal(3, DungeonManager.PickBossRoom(new long[] { 1, 2, 3 }, names, numberedLast: 2));
        }

        [Fact]
        public void PickBossRoom_never_returns_an_Exit()
        {
            var names = new Dictionary<long, string>
            {
                [1] = "Magik Riktus Big Top - First Room",
                [2] = "Magik Riktus Big Top - Fifth Room",
                [3] = "Magik Riktus Big Top - Exit",
            };

            Assert.Equal(2, DungeonManager.PickBossRoom(new long[] { 3, 1, 2 }, names, numberedLast: 2));
            Assert.True(DungeonManager.IsExitName("Magik Riktus Big Top - Exit"));
        }

        [Fact]
        public void SameDungeonRoom_is_true_for_two_rooms_of_one_dungeon()
        {
            if (!File.Exists(Paths.DungeonsJson)) return;

            DungeonManager.Initialize();
            var sandy = DungeonManager.Get(19);
            if (sandy == null || sandy.Rooms.Count < 2) return;

            Assert.True(QuestWatcher.SameDungeonRoom(sandy.Rooms[0], sandy.Rooms[^1]));
            Assert.False(QuestWatcher.SameDungeonRoom(sandy.Rooms[0], 0));
        }

        [Fact]
        public void Live_catalogue_puts_bosses_on_the_maps_quests_name()
        {
            if (!File.Exists(Paths.DungeonsJson)) return;

            DungeonManager.Initialize();
            if (!DungeonManager.IsLoaded) return;

            // Castillo de Arena / Sandy Castle — quest 896 wants Fifth Room.
            Assert.Equal(193729536, DungeonManager.Get(19)?.LastRoom);

            // Crackler's Rocky Peaks — Seventh Room.
            Assert.Equal(106960896, DungeonManager.Get(18)?.LastRoom);

            // Koolich Cavern — Fifth Room of the cavern (not the lair, not Exit).
            Assert.Equal(107227136, DungeonManager.Get(30)?.LastRoom);

            // Lord Crow — Throne Room.
            Assert.Equal(159125512, DungeonManager.Get(3)?.LastRoom);

            // Ilyzaelle — Erzal's Fissure.
            Assert.Equal(184686337, DungeonManager.Get(107)?.LastRoom);

            // Magik Riktus — Fifth Room, never the Exit.
            Assert.Equal(181669888, DungeonManager.Get(105)?.LastRoom);
        }

        /// <summary>
        /// Lord Crow's library as Ankama remade it: written over the catalogue's dungeon 3, its
        /// sixteen rooms walked freely, Virgil at the First Room's door, Capsálocke in the annexe
        /// with his own companions and the Master in his throne room with the library's three.
        /// </summary>
        [Fact]
        public void Live_catalogue_has_the_remade_library()
        {
            if (!File.Exists(Paths.DungeonsJson)) return;

            DungeonManager.Initialize();
            if (!DungeonManager.IsLoaded) return;

            var library = DungeonManager.Get(3);
            Assert.NotNull(library);
            Assert.True(library!.Authored);
            Assert.True(library.KeepsADoor);
            Assert.Equal(new[] { 159121666L, 159125512L }, library.Rooms);
            Assert.Equal(new[] { 4572 }, library.WrittenQuarry[159121666]);
            Assert.Equal(new[] { 4578, 4579, 4580 }, library.WrittenRoster[159121666]);
            Assert.Equal(new[] { 823, 824, 825 }, library.WrittenRoster[159125512]);

            Assert.Same(library, DungeonManager.AtEntrance(159121408));
            Assert.False(DungeonManager.IsRoom(159122432));   // the Second Room is no longer a room of it
            Assert.True(DungeonManager.IsRoom(159121666));
        }

        /// <summary>
        /// The dungeons whose corridors already gave their order keep the last room of it: the
        /// partial names put these bosses in a Fourth or Third Room, and a win there left the
        /// dungeon before its real last room.
        /// </summary>
        [Fact]
        public void Live_catalogue_keeps_the_boss_rooms_the_corridors_give()
        {
            if (!File.Exists(Paths.DungeonsJson)) return;

            DungeonManager.Initialize();
            if (!DungeonManager.IsLoaded) return;

            Assert.Equal(169873408, DungeonManager.Get(97)?.LastRoom);   // Captain Meno's Ship - Final Room
            Assert.Equal(140776448, DungeonManager.Get(84)?.LastRoom);   // Belly of the Whale - Heart Room
            Assert.Equal(152834048, DungeonManager.Get(90)?.LastRoom);   // Kardorim's Crypt - Kardorim's Tomb
            Assert.Equal(157028352, DungeonManager.Get(91)?.LastRoom);   // LeChouque's Boat - LeChouque's Cabin
            Assert.Equal(182456320, DungeonManager.Get(96)?.LastRoom);   // Cursed Araknas Temple - Araknas Altar
            Assert.Equal(174330368, DungeonManager.Get(100)?.LastRoom);  // Mastodon Cemetery - Final Room
            Assert.Equal(62135816, DungeonManager.Get(61)?.LastRoom);    // Bearbarian Antichamber - Celestial Bearbarian's Chamber
        }

        /// <summary>Every room numbered, past the twelfth: the Dragon Pig's Den ends in a Thirteenth Room.</summary>
        [Fact]
        public void An_ordinal_past_the_twelfth_still_counts()
        {
            Assert.Equal(13, DungeonManager.OrdinalOf("Dragon Pig's Den - Thirteenth Room"));
            Assert.Equal(3, DungeonManager.OrdinalOf("Dragon Pig's Den - Third Room"));
            Assert.Equal(4, DungeonManager.OrdinalOf("Dragon Pig's Den - Fourth Room"));
        }

        [Fact]
        public void Live_catalogue_never_uses_an_Exit_as_LastRoom()
        {
            if (!File.Exists(Paths.DungeonsJson)) return;

            DungeonManager.Initialize();
            if (!DungeonManager.IsLoaded) return;

            var names = new Dictionary<long, string>();
            try
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                    Paths.WorldConnectionString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT MapId, Name FROM MapPositions WHERE Name IS NOT NULL;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) names[reader.GetInt64(0)] = reader.GetString(1);
            }
            catch
            {
                return;
            }

            if (names.Count == 0) return;

            var exitsAsBoss = DungeonManager.All.Values
                .Where(d => d.LastRoom != 0 &&
                            names.TryGetValue(d.LastRoom, out string? n) &&
                            n != null &&
                            DungeonManager.IsExitName(n))
                .Select(d => d.Id)
                .ToList();

            Assert.True(exitsAsBoss.Count == 0,
                        "Exit used as LastRoom in dungeons: " + string.Join(", ", exitsAsBoss));
        }
    }
}
