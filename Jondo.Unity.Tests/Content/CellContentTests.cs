using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// Hand-made changes to map cells: three layers, and deltas rather than copies.
    /// </summary>
    /// <remarks>
    /// The delta rule is the one worth guarding. A map has 560 cells and this file holds the ones
    /// that changed; if it ever started holding all of them it would shadow the generated file for
    /// that map for ever, and nothing anywhere would say so.
    /// </remarks>
    public class CellContentTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(),
                                                     "jondo-cells-" + Guid.NewGuid().ToString("N") + ".json");

        public void Dispose()
        {
            try { File.Delete(_path); } catch (IOException) { }
        }

        [Fact]
        public void One_layer_at_a_time_survives_a_round_trip()
        {
            var patch = new CellPatch { MapId = 700, Cell = 250, Walkable = true };
            CellContent.Save(_path, new[] { patch });

            var store = CellContent.Load(_path);
            Assert.True(store.TryGet(new CellKey(700, 250), out var row));

            Assert.True(row.Value.Walkable);
            Assert.Null(row.Value.WalkableInFight);
            Assert.Null(row.Value.BlocksSight);
        }

        [Fact]
        public void All_three_layers_can_be_said_at_once()
        {
            CellContent.Save(_path, new[]
            {
                new CellPatch { MapId = 700, Cell = 251, Walkable = false, WalkableInFight = true, BlocksSight = false },
            });

            var store = CellContent.Load(_path);
            Assert.True(store.TryGet(new CellKey(700, 251), out var row));

            Assert.False(row.Value.Walkable);
            Assert.True(row.Value.WalkableInFight);
            Assert.False(row.Value.BlocksSight);
        }

        /// <summary>
        /// A row that changes nothing is not written. Without this the file would grow every time
        /// somebody clicked a cell and clicked it back.
        /// </summary>
        [Fact]
        public void A_patch_that_says_nothing_is_not_written()
        {
            CellContent.Save(_path, new[]
            {
                new CellPatch { MapId = 700, Cell = 252 },
                new CellPatch { MapId = 700, Cell = 253, Walkable = true },
            });

            Assert.Equal(1, CellContent.Load(_path).Count);
        }

        [Fact]
        public void A_tombstone_drops_a_change()
        {
            CellContent.Save(_path, new[] { new CellPatch { MapId = 700, Cell = 254, Walkable = true } });

            // Written by hand, the way somebody editing the file would.
            string text = File.ReadAllText(_path)
                .Replace("\"walk\": true", "\"remove\": true");
            File.WriteAllText(_path, text);

            var store = CellContent.Load(_path);
            Assert.Equal(0, store.Count);
            Assert.Equal(1, store.ErasedCount);
        }

        // ─── Applying them ───────────────────────────────────────────────────────

        /// <summary>
        /// The same method the server calls at startup and the editor calls to draw its preview.
        /// One implementation, so the picture and the game cannot disagree.
        /// </summary>
        [Fact]
        public void A_patch_adds_and_removes_on_the_right_layer()
        {
            var walkable = new Dictionary<long, HashSet<int>> { [700] = new HashSet<int> { 100, 101 } };
            var inFight = new Dictionary<long, HashSet<int>> { [700] = new HashSet<int> { 100 } };
            var sight = new Dictionary<long, HashSet<int>> { [700] = new HashSet<int>() };

            CellContent.Apply(new[]
            {
                new CellPatch { MapId = 700, Cell = 102, Walkable = true },
                new CellPatch { MapId = 700, Cell = 101, Walkable = false },
                new CellPatch { MapId = 700, Cell = 100, WalkableInFight = false },
                new CellPatch { MapId = 700, Cell = 105, BlocksSight = true },
            }, walkable, inFight, sight);

            Assert.Equal(new[] { 100, 102 }, walkable[700].OrderBy(c => c));
            Assert.Empty(inFight[700]);
            Assert.Equal(new[] { 105 }, sight[700]);
        }

        /// <summary>
        /// A map nobody extracted can still be given cells. Editing a map that is missing from the
        /// generated files is a legitimate thing to want, and the commonest reason to reach for
        /// this screen at all.
        /// </summary>
        [Fact]
        public void A_map_with_no_generated_cells_can_still_be_given_some()
        {
            var walkable = new Dictionary<long, HashSet<int>>();
            var inFight = new Dictionary<long, HashSet<int>>();
            var sight = new Dictionary<long, HashSet<int>>();

            CellContent.Apply(new[] { new CellPatch { MapId = 999, Cell = 200, Walkable = true } },
                              walkable, inFight, sight);

            Assert.True(walkable.ContainsKey(999));
            Assert.Contains(200, walkable[999]);
            Assert.False(inFight.ContainsKey(999));
        }

        [Fact]
        public void A_layer_left_null_is_left_alone()
        {
            var walkable = new Dictionary<long, HashSet<int>> { [700] = new HashSet<int> { 100 } };
            var inFight = new Dictionary<long, HashSet<int>> { [700] = new HashSet<int> { 100 } };
            var sight = new Dictionary<long, HashSet<int>> { [700] = new HashSet<int> { 100 } };

            CellContent.Apply(new[] { new CellPatch { MapId = 700, Cell = 100, Walkable = false } },
                              walkable, inFight, sight);

            Assert.Empty(walkable[700]);
            Assert.Contains(100, inFight[700]);
            Assert.Contains(100, sight[700]);
        }

        // ─── Not being there ─────────────────────────────────────────────────────

        [Fact]
        public void A_missing_file_loads_as_nothing()
            => Assert.Equal(0, CellContent.Load(Path.Combine(Path.GetTempPath(), "no-such.json")).Count);

        [Fact]
        public void A_file_that_is_not_json_is_reported_and_not_thrown()
        {
            File.WriteAllText(_path, "not json at all {{{");

            string complaint = "";
            Assert.Equal(0, CellContent.Load(_path, said => complaint = said).Count);
            Assert.NotEqual("", complaint);
        }

        [Fact]
        public void The_file_it_writes_can_be_read_by_a_person()
        {
            CellContent.Save(_path, new[]
            {
                new CellPatch { MapId = 191106562, Cell = 250, Walkable = true, BlocksSight = false },
            });

            string written = File.ReadAllText(_path);
            Assert.Contains("DELTAS, NOT COPIES", written);
            Assert.Contains("\"walk\": true", written);
            Assert.Contains("\"sight\": false", written);
            Assert.DoesNotContain("\"fight\"", written);
        }
    }
}
