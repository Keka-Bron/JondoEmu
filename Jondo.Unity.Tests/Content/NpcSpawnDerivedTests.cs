using System;
using System.Collections.Generic;
using System.IO;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// The base layer: NPC placements worked out from the quest catalogue rather than captured.
    /// </summary>
    /// <remarks>
    /// These exist because the base layer is the one that can do real damage quietly. It carries
    /// 2,009 rows against the measured file's 422, and every one of them has a guessed cell — so
    /// if it ever outranked a capture, or if the editor ever mistook it for something a person had
    /// changed, the world would fill up with NPCs standing in the wrong place and the files that
    /// know better would stop reaching it. Neither failure shows up while you are using the editor;
    /// both show up months later as "why is this NPC over there".
    /// </remarks>
    public class NpcSpawnDerivedTests : IDisposable
    {
        private readonly string _folder;

        public NpcSpawnDerivedTests()
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

        private string Derived(string rows) => Write("derived.json", "{\"spawns\":[" + rows + "]}");

        private string Measured(string rows) => Write("measured.json", "{\"npcs\":[" + rows + "]}");

        private string Authored(string rows) => Write("authored.json", "{\"spawns\":[" + rows + "]}");

        // ─── It loads at all ──────────────────────────────────────────────────────

        [Fact]
        public void A_derived_row_lands_in_the_base_layer()
        {
            string path = Derived("{\"map\":1,\"npc\":7,\"cell\":100,\"orientation\":3,\"why\":\"gives quest 42\"}");

            var store = NpcSpawnContent.Load(null, null, null, path);

            Assert.Equal(1, store.Count);
            Assert.True(store.TryGet(new NpcSpawnKey(1, 7, 100), out var row));
            Assert.Equal(ContentLayer.Base, row.From.Layer);
            Assert.Equal(3, row.Value.Orientation);
        }

        [Fact]
        public void The_extra_why_field_is_ignored_rather_than_fatal()
        {
            // The generator writes `why` and `weak` for a person to read. A loader that fell over
            // on a field it did not recognise would make the file unextendable.
            string path = Derived("{\"map\":1,\"npc\":7,\"cell\":100,\"why\":\"x\",\"weak\":true}");

            var store = NpcSpawnContent.Load(null, null, null, path);

            Assert.Equal(1, store.Count);
        }

        [Fact]
        public void A_derived_row_with_no_cell_is_dropped_not_defaulted()
        {
            // The cell is part of the key. A row falling back to cell 0 would not collide with the
            // same NPC placed properly, it would stand next to it, and the map would show two.
            string path = Derived("{\"map\":1,\"npc\":7},{\"map\":1,\"npc\":8,\"cell\":100}");

            var store = NpcSpawnContent.Load(null, null, null, path);

            Assert.Equal(1, store.Count);
            Assert.False(store.TryGet(new NpcSpawnKey(1, 7, 0), out _));
        }

        [Fact]
        public void A_broken_derived_file_complains_instead_of_throwing()
        {
            string path = Write("derived.json", "{\"spawns\":[{\"map\":1,");
            var said = new List<string>();

            var store = NpcSpawnContent.Load(null, null, said.Add, path);

            Assert.Equal(0, store.Count);
            Assert.Single(said);
        }

        // ─── Precedence, which is the whole point ─────────────────────────────────

        [Fact]
        public void A_capture_beats_a_derived_row_on_the_same_key()
        {
            string derived = Derived("{\"map\":1,\"npc\":7,\"cell\":100,\"orientation\":3}");
            string measured = Measured("{\"mapa\":1,\"npc\":7,\"casilla\":100,\"orientacion\":6}");

            var store = NpcSpawnContent.Load(measured, null, null, derived);

            Assert.True(store.TryGet(new NpcSpawnKey(1, 7, 100), out var row));
            Assert.Equal(ContentLayer.Measured, row.From.Layer);
            Assert.Equal(6, row.Value.Orientation);
        }

        [Fact]
        public void Load_order_cannot_change_who_wins()
        {
            // The layers decide, not the sequence the loaders happen to run in. This is the rule
            // that stops a reshuffle of startup silently undoing an authored row.
            string derived = Derived("{\"map\":1,\"npc\":7,\"cell\":100,\"orientation\":3}");
            string authored = Authored("{\"map\":1,\"npc\":7,\"cell\":100,\"orientation\":5}");

            var store = NpcSpawnContent.Load(null, authored, null, derived);

            Assert.True(store.TryGet(new NpcSpawnKey(1, 7, 100), out var row));
            Assert.Equal(ContentLayer.Authored, row.From.Layer);
            Assert.Equal(5, row.Value.Orientation);
        }

        [Fact]
        public void An_authored_tombstone_removes_a_derived_row()
        {
            // Taking out an NPC the derivation guessed wrong has to work without editing the
            // generated file, because a tool rewrites that file.
            string derived = Derived("{\"map\":1,\"npc\":7,\"cell\":100}");
            string authored = Authored("{\"map\":1,\"npc\":7,\"cell\":100,\"remove\":true}");

            var store = NpcSpawnContent.Load(null, authored, null, derived);

            Assert.Equal(0, store.Count);
            Assert.Equal(1, store.ErasedCount);
        }

        [Fact]
        public void A_derived_row_on_another_map_stands_next_to_a_captured_one()
        {
            // Not a collision: the same NPC really can stand on more than one map, and the two
            // sources describing different maps is agreement, not conflict.
            string derived = Derived("{\"map\":2,\"npc\":7,\"cell\":100}");
            string measured = Measured("{\"mapa\":1,\"npc\":7,\"casilla\":250}");

            var store = NpcSpawnContent.Load(measured, null, null, derived);

            Assert.Equal(2, store.Count);
        }

        // ─── The delta, which is where the damage would be ────────────────────────

        [Fact]
        public void An_untouched_derived_row_is_not_written_to_the_authored_file()
        {
            // The one that matters. If the delta floor left the base layer out, opening the page
            // and saving would copy all 2,009 derived rows into the authored file, and from then
            // on re-running the derivation would never reach the world again.
            var generated = new Dictionary<NpcSpawnKey, NpcSpawn>
            {
                [new NpcSpawnKey(1, 7, 100)] = new NpcSpawn
                {
                    MapId = 1, NpcId = 7, Cell = 100, Orientation = 1,
                },
            };

            var (rows, removed) = NpcSpawnContent.Delta(generated, generated);

            Assert.Empty(rows);
            Assert.Empty(removed);
        }

        [Fact]
        public void Moving_a_derived_npc_writes_one_row_and_one_tombstone()
        {
            var generated = new Dictionary<NpcSpawnKey, NpcSpawn>
            {
                [new NpcSpawnKey(1, 7, 100)] = new NpcSpawn
                {
                    MapId = 1, NpcId = 7, Cell = 100, Orientation = 1,
                },
            };
            var wanted = new Dictionary<NpcSpawnKey, NpcSpawn>
            {
                [new NpcSpawnKey(1, 7, 264)] = new NpcSpawn
                {
                    MapId = 1, NpcId = 7, Cell = 264, Orientation = 1,
                },
            };

            var (rows, removed) = NpcSpawnContent.Delta(generated, wanted);

            Assert.Single(rows);
            Assert.Equal(264, rows[0].Cell);
            Assert.Single(removed);
            Assert.Equal(100, removed[0].Cell);
        }

        // ─── The census, which is what the log and the editor read ────────────────

        [Fact]
        public void The_census_counts_the_three_layers_apart()
        {
            string derived = Derived("{\"map\":1,\"npc\":7,\"cell\":100},{\"map\":1,\"npc\":8,\"cell\":101}");
            string measured = Measured("{\"mapa\":2,\"npc\":9,\"casilla\":200}");
            string authored = Authored("{\"map\":3,\"npc\":10,\"cell\":300}");

            var census = NpcSpawnContent.Load(measured, authored, null, derived).Census();

            Assert.Equal(2, census[ContentLayer.Base]);
            Assert.Equal(1, census[ContentLayer.Measured]);
            Assert.Equal(1, census[ContentLayer.Authored]);
        }
    }
}
