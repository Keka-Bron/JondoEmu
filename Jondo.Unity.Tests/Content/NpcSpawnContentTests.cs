using System;
using System.Collections.Generic;
using System.IO;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// Loading NPC placements off disk: the two file formats, and every way a file can be wrong.
    /// </summary>
    /// <remarks>
    /// The loader runs during startup, before anything else is ready, so nothing it meets on disk
    /// may take the server down: a missing file, an empty one, a truncated one, a row with fields
    /// missing. Those are the cases here, next to the merge behaviour itself.
    /// </remarks>
    public class NpcSpawnContentTests : IDisposable
    {
        private readonly string _folder;

        public NpcSpawnContentTests()
        {
            _folder = Path.Combine(Path.GetTempPath(), "jondo-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        public void Dispose()
        {
            try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
        }

        private string Write(string name, string json)
        {
            string path = Path.Combine(_folder, name);
            File.WriteAllText(path, json);
            return path;
        }

        private static List<string> Complaints(out Action<string> report)
        {
            var said = new List<string>();
            report = said.Add;
            return said;
        }

        // ─── Nothing on disk ──────────────────────────────────────────────────────

        [Fact]
        public void No_files_at_all_gives_an_empty_store_and_no_exception()
        {
            var store = NpcSpawnContent.Load(null, null);
            Assert.Equal(0, store.Count);
        }

        [Fact]
        public void Files_that_do_not_exist_are_not_an_error()
        {
            var store = NpcSpawnContent.Load(Path.Combine(_folder, "nope.json"),
                                             Path.Combine(_folder, "also-nope.json"));
            Assert.Equal(0, store.Count);
        }

        [Fact]
        public void A_truncated_file_is_reported_and_does_not_throw()
        {
            string path = Write("broken.json", "{ \"npcs\": [ { \"mapa\": 1,");
            var said = Complaints(out var report);

            var store = NpcSpawnContent.Load(path, null, report);

            Assert.Equal(0, store.Count);
            Assert.Single(said);
            Assert.Contains("unreadable", said[0]);
        }

        [Fact]
        public void A_file_without_the_expected_array_is_ignored()
        {
            string path = Write("other.json", "{ \"something\": 1 }");
            var store = NpcSpawnContent.Load(path, null);
            Assert.Equal(0, store.Count);
        }

        // ─── The measured layer ───────────────────────────────────────────────────

        [Fact]
        public void Measured_rows_load_with_their_cell_and_facing()
        {
            string path = Write("measured.json",
                "{ \"npcs\": [ { \"mapa\": 100, \"npc\": 7, \"casilla\": 260, \"orientacion\": 3 } ] }");

            var store = NpcSpawnContent.Load(path, null);

            Assert.True(store.TryGet(new NpcSpawnKey(100, 7, 260), out var row));
            Assert.Equal(3, row.Value.Orientation);
            Assert.Equal(ContentLayer.Measured, row.From.Layer);
        }

        [Fact]
        public void A_measured_row_without_a_facing_gets_the_default()
        {
            string path = Write("measured.json", "{ \"npcs\": [ { \"mapa\": 100, \"npc\": 7, \"casilla\": 260 } ] }");
            var store = NpcSpawnContent.Load(path, null);

            Assert.True(store.TryGet(new NpcSpawnKey(100, 7, 260), out var row));
            Assert.Equal(1, row.Value.Orientation);
        }

        [Theory]
        [InlineData("{ \"npcs\": [ { \"npc\": 7, \"casilla\": 260 } ] }")]            // no map
        [InlineData("{ \"npcs\": [ { \"mapa\": 100, \"casilla\": 260 } ] }")]         // no npc
        public void Rows_missing_what_identifies_them_are_skipped(string json)
        {
            var store = NpcSpawnContent.Load(Write("m.json", json), null);
            Assert.Equal(0, store.Count);
        }

        /// <summary>
        /// The same NPC really can stand more than once on one map, and forgetting it cost 26 of
        /// the 422 captured placements for about ten minutes. NPC 7629 stands three times on map
        /// 99090957, on cells 248, 263 and 277.
        /// </summary>
        [Fact]
        public void The_same_npc_can_stand_several_times_on_one_map()
        {
            string path = Write("measured.json",
                "{ \"npcs\": [ { \"mapa\": 99090957, \"npc\": 7629, \"casilla\": 248 }," +
                "              { \"mapa\": 99090957, \"npc\": 7629, \"casilla\": 263 }," +
                "              { \"mapa\": 99090957, \"npc\": 7629, \"casilla\": 277 } ] }");

            var store = NpcSpawnContent.Load(path, null);

            Assert.Equal(3, store.Count);
        }

        // ─── The authored layer ───────────────────────────────────────────────────

        [Fact]
        public void An_authored_row_replaces_the_measured_one_on_the_same_cell()
        {
            string measured = Write("m.json",
                "{ \"npcs\": [ { \"mapa\": 100, \"npc\": 7, \"casilla\": 260, \"orientacion\": 3 } ] }");
            string authored = Write("a.json",
                "{ \"spawns\": [ { \"map\": 100, \"npc\": 7, \"cell\": 260, \"orientation\": 5 } ] }");

            var store = NpcSpawnContent.Load(measured, authored);

            Assert.True(store.TryGet(new NpcSpawnKey(100, 7, 260), out var row));
            Assert.Equal(5, row.Value.Orientation);
            Assert.Equal(ContentLayer.Authored, row.From.Layer);
            Assert.Equal(1, store.Count);
        }

        [Fact]
        public void An_authored_row_inherits_what_it_leaves_out()
        {
            string measured = Write("m.json",
                "{ \"npcs\": [ { \"mapa\": 100, \"npc\": 7, \"casilla\": 260, \"orientacion\": 3 } ] }");
            string authored = Write("a.json",
                "{ \"spawns\": [ { \"map\": 100, \"npc\": 7, \"cell\": 260 } ] }");

            var store = NpcSpawnContent.Load(measured, authored);

            Assert.True(store.TryGet(new NpcSpawnKey(100, 7, 260), out var row));
            Assert.Equal(3, row.Value.Orientation);
        }

        [Fact]
        public void An_authored_row_on_a_cell_nobody_measured_is_a_brand_new_placement()
        {
            string authored = Write("a.json",
                "{ \"spawns\": [ { \"map\": 500, \"npc\": 9, \"cell\": 111 } ] }");

            var store = NpcSpawnContent.Load(null, authored);

            Assert.True(store.TryGet(new NpcSpawnKey(500, 9, 111), out var row));
            Assert.Equal(1, row.Value.Orientation);
        }

        [Fact]
        public void An_authored_row_without_a_cell_is_reported_and_skipped()
        {
            // The cell identifies the placement. Without it there is no way to know which of an
            // NPC's several placements the row means, and guessing would move the wrong one.
            string authored = Write("a.json", "{ \"spawns\": [ { \"map\": 100, \"npc\": 7 } ] }");
            var said = Complaints(out var report);

            var store = NpcSpawnContent.Load(null, authored, report);

            Assert.Equal(0, store.Count);
            Assert.Single(said);
            Assert.Contains("no cell", said[0]);
        }

        [Fact]
        public void A_tombstone_removes_the_measured_placement()
        {
            string measured = Write("m.json",
                "{ \"npcs\": [ { \"mapa\": 100, \"npc\": 7, \"casilla\": 260 }," +
                "              { \"mapa\": 100, \"npc\": 7, \"casilla\": 300 } ] }");
            string authored = Write("a.json",
                "{ \"spawns\": [ { \"map\": 100, \"npc\": 7, \"cell\": 260, \"remove\": true } ] }");

            var store = NpcSpawnContent.Load(measured, authored);

            Assert.False(store.Contains(new NpcSpawnKey(100, 7, 260)));
            Assert.True(store.Contains(new NpcSpawnKey(100, 7, 300)));
            Assert.Equal(1, store.ErasedCount);
        }

        [Fact]
        public void Moving_an_npc_is_a_tombstone_plus_a_row()
        {
            string measured = Write("m.json",
                "{ \"npcs\": [ { \"mapa\": 100, \"npc\": 7, \"casilla\": 260, \"orientacion\": 3 } ] }");
            string authored = Write("a.json",
                "{ \"spawns\": [ { \"map\": 100, \"npc\": 7, \"cell\": 260, \"remove\": true }," +
                "                { \"map\": 100, \"npc\": 7, \"cell\": 301, \"orientation\": 3 } ] }");

            var store = NpcSpawnContent.Load(measured, authored);

            Assert.Equal(1, store.Count);
            Assert.True(store.Contains(new NpcSpawnKey(100, 7, 301)));
        }

        [Fact]
        public void Remove_false_is_a_placement_and_not_a_tombstone()
        {
            string authored = Write("a.json",
                "{ \"spawns\": [ { \"map\": 100, \"npc\": 7, \"cell\": 260, \"remove\": false } ] }");

            var store = NpcSpawnContent.Load(null, authored);

            Assert.Equal(1, store.Count);
            Assert.Equal(0, store.ErasedCount);
        }
    }
}
