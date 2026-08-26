using System;
using System.Collections.Generic;
using System.IO;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// Writing NPC placements back out as a difference, never as a copy.
    /// </summary>
    /// <remarks>
    /// This is the rule that keeps the authored layer worth having, and it is tested rather than
    /// trusted because breaking it is invisible. An editor that wrote out every placement it was
    /// showing would produce a file with all 422 rows in it, and from that moment the measured file
    /// would never reach the world again: somebody re-runs the extraction to fix a cell, the fix
    /// lands, and it is shadowed by a copy nobody remembers making. Nothing fails, nothing is
    /// logged, and the world is quietly frozen.
    /// </remarks>
    public class NpcSpawnDeltaTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(),
                                                     "jondo-spawns-" + Guid.NewGuid().ToString("N") + ".json");

        public void Dispose()
        {
            try { File.Delete(_path); } catch (IOException) { }
        }

        private static NpcSpawnKey Key(long map, int npc, int cell) => new NpcSpawnKey(map, npc, cell);

        private static NpcSpawn Spawn(long map, int npc, int cell, int facing = 1) => new NpcSpawn
        {
            MapId = map, NpcId = npc, Cell = cell, Orientation = facing,
        };

        private static Dictionary<NpcSpawnKey, NpcSpawn> Set(params NpcSpawn[] spawns)
        {
            var map = new Dictionary<NpcSpawnKey, NpcSpawn>();
            foreach (var spawn in spawns) map[Key(spawn.MapId, spawn.NpcId, spawn.Cell)] = spawn;
            return map;
        }

        // ─── What must not be written ─────────────────────────────────────────────

        /// <summary>The whole point. Nothing to say means nothing written.</summary>
        [Fact]
        public void A_world_that_matches_the_captures_writes_nothing()
        {
            var measured = Set(Spawn(1, 100, 260), Spawn(1, 101, 275));
            var (rows, removed) = NpcSpawnContent.Delta(measured, Set(Spawn(1, 100, 260), Spawn(1, 101, 275)));

            Assert.Empty(rows);
            Assert.Empty(removed);
        }

        [Fact]
        public void Untouched_placements_stay_out_of_the_file_even_when_others_change()
        {
            var measured = Set(Spawn(1, 100, 260), Spawn(1, 101, 275), Spawn(2, 102, 300));
            var wanted = Set(Spawn(1, 100, 260), Spawn(1, 101, 275), Spawn(2, 102, 300), Spawn(3, 103, 12));

            var (rows, removed) = NpcSpawnContent.Delta(measured, wanted);

            var row = Assert.Single(rows);
            Assert.Equal(103, row.NpcId);
            Assert.Empty(removed);
        }

        // ─── What must be ─────────────────────────────────────────────────────────

        [Fact]
        public void A_placement_that_was_not_measured_is_written()
        {
            var (rows, _) = NpcSpawnContent.Delta(Set(), Set(Spawn(1, 100, 260, facing: 3)));

            var row = Assert.Single(rows);
            Assert.Equal(260, row.Cell);
            Assert.Equal(3, row.Orientation);
        }

        [Fact]
        public void Turning_a_measured_npc_around_is_written()
        {
            var measured = Set(Spawn(1, 100, 260, facing: 1));
            var (rows, removed) = NpcSpawnContent.Delta(measured, Set(Spawn(1, 100, 260, facing: 5)));

            Assert.Equal(5, Assert.Single(rows).Orientation);
            Assert.Empty(removed);
        }

        [Fact]
        public void A_measured_placement_that_is_gone_becomes_a_tombstone()
        {
            var measured = Set(Spawn(1, 100, 260), Spawn(1, 101, 275));
            var (rows, removed) = NpcSpawnContent.Delta(measured, Set(Spawn(1, 100, 260)));

            Assert.Empty(rows);
            Assert.Equal(Key(1, 101, 275), Assert.Single(removed));
        }

        /// <summary>
        /// Moving is two operations because the cell is part of the key: the same NPC can stand
        /// several times on one map, and 18 of them do. A key without the cell lost 26 of the 422
        /// measured placements the first time round.
        /// </summary>
        [Fact]
        public void Moving_one_is_a_tombstone_and_a_row()
        {
            var measured = Set(Spawn(1, 100, 260));
            var (rows, removed) = NpcSpawnContent.Delta(measured, Set(Spawn(1, 100, 275)));

            Assert.Equal(275, Assert.Single(rows).Cell);
            Assert.Equal(Key(1, 100, 260), Assert.Single(removed));
        }

        [Fact]
        public void The_same_npc_twice_on_one_map_stays_two_placements()
        {
            var measured = Set(Spawn(99090957, 7629, 248), Spawn(99090957, 7629, 263));
            var (rows, removed) = NpcSpawnContent.Delta(measured, Set(Spawn(99090957, 7629, 248)));

            Assert.Empty(rows);
            Assert.Equal(263, Assert.Single(removed).Cell);
        }

        // ─── And it all comes back ────────────────────────────────────────────────

        [Fact]
        public void What_the_delta_says_is_what_the_file_produces()
        {
            var measured = Set(Spawn(1, 100, 260), Spawn(1, 101, 275));
            var wanted = Set(Spawn(1, 100, 260, facing: 7), Spawn(2, 200, 12));

            var (rows, removed) = NpcSpawnContent.Delta(measured, wanted);
            NpcSpawnContent.Save(_path, rows, removed);

            // Read back through the real loader, measured layer and all, and the world that comes
            // out has to be the one that was asked for.
            var store = new ContentStore<NpcSpawnKey, NpcSpawn>();
            foreach (var pair in measured) store.Put(pair.Key, pair.Value, Origin.Measured("a capture"));

            var merged = NpcSpawnContent.Load(null, _path);
            foreach (var pair in measured) merged.Put(pair.Key, pair.Value, Origin.Measured("a capture"));

            Assert.Equal(wanted.Count, merged.Count);
            foreach (var pair in wanted)
            {
                Assert.True(merged.TryGet(pair.Key, out var row), $"{pair.Key} is missing");
                Assert.Equal(pair.Value.Orientation, row.Value.Orientation);
            }
        }

        [Fact]
        public void The_order_does_not_depend_on_the_order_they_were_added()
        {
            var one = new[] { Spawn(30, 1, 5), Spawn(10, 2, 6) };
            var other = new[] { Spawn(10, 2, 6), Spawn(30, 1, 5) };

            NpcSpawnContent.Save(_path, one, Array.Empty<NpcSpawnKey>());
            string first = File.ReadAllText(_path);
            NpcSpawnContent.Save(_path, other, Array.Empty<NpcSpawnKey>());

            Assert.Equal(first, File.ReadAllText(_path));
        }

        [Fact]
        public void Saving_leaves_no_half_written_file_behind()
        {
            NpcSpawnContent.Save(_path, new[] { Spawn(1, 1, 1) }, Array.Empty<NpcSpawnKey>());

            Assert.True(File.Exists(_path));
            Assert.False(File.Exists(_path + ".writing"));
        }

        /// <summary>
        /// A tombstone that is read and not written back out again would let the next save undo the
        /// removal, which is the silent kind of loss the layers exist to prevent.
        /// </summary>
        [Fact]
        public void Tombstones_survive_a_load_and_a_save()
        {
            NpcSpawnContent.Save(_path, Array.Empty<NpcSpawn>(), new[] { Key(1, 100, 260) });

            var store = NpcSpawnContent.Load(null, _path);
            var kept = new List<NpcSpawnKey>(store.ErasedKeys);

            Assert.Equal(Key(1, 100, 260), Assert.Single(kept));

            NpcSpawnContent.Save(_path, Array.Empty<NpcSpawn>(), kept);
            Assert.Equal(1, NpcSpawnContent.Load(null, _path).ErasedCount);
        }
    }
}
