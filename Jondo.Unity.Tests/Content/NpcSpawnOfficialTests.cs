using System;
using System.IO;
using System.Linq;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// The official layer, <c>content/npcs/official.json</c>: the NPCs the official server listed
    /// on a map's contents (packet captures of walks through the world), with their cell and facing.
    /// On a listed map it is the whole truth: guessed, derived and hand-placed rows that it does not
    /// hold are taken out, and the listed ones are put exactly as given.
    /// </summary>
    public class NpcSpawnOfficialTests : IDisposable
    {
        private readonly string _folder;

        public NpcSpawnOfficialTests()
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

        [Fact]
        public void A_listed_map_holds_exactly_the_official_rows()
        {
            string derived = Write("derived.json", "{\"spawns\":[{\"map\":1,\"npc\":7,\"cell\":100},{\"map\":2,\"npc\":8,\"cell\":50}]}");
            string authored = Write("authored.json", "{\"spawns\":[{\"map\":1,\"npc\":9,\"cell\":218,\"orientation\":1}]}");
            string official = Write("official.json",
                "{\"maps\":[{\"map\":1,\"spawns\":[{\"npc\":9,\"cell\":330,\"orientation\":1},{\"npc\":10,\"cell\":343,\"orientation\":3,\"criterion\":\"Qa=1918\"}]}]}");

            var store = NpcSpawnContent.Load(null, authored, null, derived, official);

            var onMap1 = store.Rows.Where(r => r.Key.MapId == 1).Select(r => r.Key).ToList();
            Assert.Equal(2, onMap1.Count);
            Assert.True(store.TryGet(new NpcSpawnKey(1, 9, 330), out var moved));
            Assert.Equal(ContentLayer.Authored, moved.From.Layer);
            Assert.True(store.TryGet(new NpcSpawnKey(1, 10, 343), out var gated));
            Assert.Equal(3, gated.Value.Orientation);
            Assert.Equal("Qa=1918", gated.Value.Criterion);
            Assert.False(store.Contains(new NpcSpawnKey(1, 7, 100)));   // derived guess, not shown
            Assert.False(store.Contains(new NpcSpawnKey(1, 9, 218)));   // hand-placed cell, superseded

            Assert.True(store.Contains(new NpcSpawnKey(2, 8, 50)));     // an unlisted map is untouched
        }

        [Fact]
        public void A_listed_map_with_no_npcs_is_emptied()
        {
            string derived = Write("derived.json", "{\"spawns\":[{\"map\":3,\"npc\":7,\"cell\":100}]}");
            string official = Write("official.json", "{\"maps\":[{\"map\":3,\"spawns\":[]}]}");

            var store = NpcSpawnContent.Load(null, null, null, derived, official);

            Assert.DoesNotContain(store.Rows, r => r.Key.MapId == 3);
        }

        [Fact]
        public void A_hand_placed_npc_behind_an_event_stays_on_a_listed_map()
        {
            // The captures were walked outside the yearly events: the event's NPC was not there to see.
            string authored = Write("authored.json",
                "{\"spawns\":[{\"map\":4,\"npc\":11,\"cell\":275,\"criterion\":\"Sc=90001\"},{\"map\":4,\"npc\":12,\"cell\":200}]}");
            string official = Write("official.json", "{\"maps\":[{\"map\":4,\"spawns\":[]}]}");

            var store = NpcSpawnContent.Load(null, authored, null, null, official);

            Assert.True(store.TryGet(new NpcSpawnKey(4, 11, 275), out var boatman));
            Assert.Equal("Sc=90001", boatman.Value.Criterion);
            Assert.False(store.Contains(new NpcSpawnKey(4, 12, 200)));
        }

        [Fact]
        public void A_hand_placed_npc_for_one_quest_step_stays_on_a_listed_map()
        {
            // The walking character was not on that step of the quest, so the NPC was not listed.
            string authored = Write("authored.json",
                "{\"spawns\":[{\"map\":4,\"npc\":11,\"cell\":289,\"criterion\":\"Qa=7&Qo>3&Qo<6\"}," +
                "{\"map\":4,\"npc\":12,\"cell\":200,\"criterion\":\"PL>50\"}," +
                "{\"map\":4,\"npc\":13,\"cell\":300,\"criterion\":\"Qa!9\"}]}");
            string official = Write("official.json",
                "{\"maps\":[{\"map\":4,\"spawns\":[{\"npc\":13,\"cell\":350}]}]}");

            var store = NpcSpawnContent.Load(null, authored, null, null, official);

            Assert.True(store.TryGet(new NpcSpawnKey(4, 11, 289), out var helper));
            Assert.Equal("Qa=7&Qo>3&Qo<6", helper.Value.Criterion);
            Assert.False(store.Contains(new NpcSpawnKey(4, 12, 200)));
            // The official map lists npc 13 elsewhere: its cell wins, no second copy.
            Assert.False(store.Contains(new NpcSpawnKey(4, 13, 300)));
            Assert.True(store.Contains(new NpcSpawnKey(4, 13, 350)));
        }

        /// <summary>The shipped Mistigris on the Sramvil cliff, on a map the official layer lists.</summary>
        [Fact]
        public void Mistigris_on_the_cliff_survives_the_official_layer()
        {
            var store = NpcSpawnContent.Load(null, Jondo.Unity.Launcher.Paths.ContentFile(NpcSpawnContent.AuthoredFile), null, null,
                Jondo.Unity.Launcher.Paths.ContentFile(NpcSpawnContent.OfficialFile));

            Assert.True(store.Contains(new NpcSpawnKey(87818757, 2769, 289)));
        }

        /// <summary>The shipped Kwismas boatman in Madrestam's port, on a map the official layer lists.</summary>
        [Fact]
        public void The_kwismas_boatman_survives_the_official_layer()
        {
            var store = NpcSpawnContent.Load(null, Jondo.Unity.Launcher.Paths.ContentFile(NpcSpawnContent.AuthoredFile), null, null,
                Jondo.Unity.Launcher.Paths.ContentFile(NpcSpawnContent.OfficialFile));

            Assert.True(store.TryGet(new NpcSpawnKey(68420097, 795, 275), out var boatman));
            Assert.Equal("Sc=90001", boatman.Value.Criterion);
        }

        [Fact]
        public void Only_the_authored_layer_overrides()
        {
            var store = new ContentStore<NpcSpawnKey, NpcSpawn>();
            Assert.Throws<InvalidOperationException>(() =>
                store.Override(new NpcSpawnKey(1, 1, 1), new NpcSpawn(), Origin.Base("x")));
        }

        /// <summary>
        /// The shipped file: the two tower concierges stand on the cells the official server showed
        /// (Bonta 330, Brâkmar 283), not on the cells inferred from a guide (218, 297).
        /// </summary>
        [Theory]
        [InlineData(213910026L, 4056, 330)]
        [InlineData(215744780L, 4055, 283)]
        public void The_shipped_file_places_the_measured_cells(long map, int npc, int cell)
        {
            string root = Jondo.Unity.Launcher.Paths.ContentFile(NpcSpawnContent.OfficialFile);
            Assert.True(File.Exists(root));

            var store = NpcSpawnContent.Load(null, null, null, null, root);

            Assert.True(store.Contains(new NpcSpawnKey(map, npc, cell)));
        }
    }
}
