using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// The authored layer for passages between maps.
    /// </summary>
    /// <remarks>
    /// The numbers pinned here are measured, and two of them corrected something this project had
    /// been repeating: a new passage declares skill <b>184</b> and type <b>-1</b>, not the 114 and
    /// 0 that every extracted row carries.
    /// </remarks>
    public class TeleportContentTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(),
                                                     "jondo-teleports-" + Guid.NewGuid().ToString("N") + ".json");

        public void Dispose()
        {
            try { File.Delete(_path); } catch (IOException) { }
        }

        private static Passage A(long map = 100, long element = 5, long toMap = 200, int toCell = 300)
            => new Passage
            {
                SourceMapId = map,
                ElementId = element,
                SourceCell = 42,
                GfxId = 1234,
                InteractiveType = TeleportContent.DefaultType,
                SkillId = TeleportContent.DefaultSkill,
                DestinationMapId = toMap,
                DestinationCell = toCell,
            };

        [Fact]
        public void A_passage_survives_a_round_trip()
        {
            TeleportContent.Save(_path, new[] { A() }, Array.Empty<PassageKey>());

            var store = TeleportContent.Load(_path);
            Assert.Equal(1, store.Count);

            Assert.True(store.TryGet(new PassageKey(100, 5), out var row));
            var passage = row.Value;

            Assert.Equal(42, passage.SourceCell);
            Assert.Equal(1234, passage.GfxId);
            Assert.Equal(200, passage.DestinationMapId);
            Assert.Equal(300, passage.DestinationCell);
            Assert.Equal(TeleportContent.DefaultSkill, passage.SkillId);
            Assert.Equal(ContentLayer.Authored, row.From.Layer);
        }

        /// <summary>
        /// The two legs of a passage are two rows, and they have to be: the server keys every route
        /// by (map, element) and has no idea a pair exists.
        /// </summary>
        [Fact]
        public void The_two_legs_are_two_rows()
        {
            var there = A(100, 5, 200, 300);
            var back = A(200, 9, 100, 42);

            TeleportContent.Save(_path, new[] { there, back }, Array.Empty<PassageKey>());

            var store = TeleportContent.Load(_path);
            Assert.Equal(2, store.Count);
            Assert.True(store.Contains(new PassageKey(100, 5)));
            Assert.True(store.Contains(new PassageKey(200, 9)));
        }

        [Fact]
        public void A_tombstone_takes_an_extracted_passage_away()
        {
            TeleportContent.Save(_path, Array.Empty<Passage>(), new[] { new PassageKey(100, 5) });

            var store = TeleportContent.Load(_path);
            Assert.Equal(0, store.Count);
            Assert.Equal(1, store.ErasedCount);
            Assert.Contains(new PassageKey(100, 5), store.ErasedKeys);
        }

        /// <summary>
        /// Skill 184, not 114.
        /// </summary>
        /// <remarks>
        /// Measured three ways that agree: Ankama's world graph uses 184 on 5,629 of 5,719
        /// interactive transitions and 114 on none; across 401 captures 184 shows up on 420
        /// elements and 114 on 23, every one of them a zaap; and in our own traffic, skill 184 is
        /// followed by a map change while 114 is followed by the zaap window. Our emitter was
        /// sending the pair (0, 114), which occurs zero times anywhere real.
        /// </remarks>
        [Fact]
        public void A_new_passage_declares_the_skill_that_was_measured()
        {
            Assert.Equal(184, TeleportContent.DefaultSkill);
            Assert.Equal(114, TeleportContent.ExtractedSkill);
            Assert.NotEqual(TeleportContent.DefaultSkill, TeleportContent.ExtractedSkill);

            // Type 0 appears zero times in the 154 types observed. -1 is the common one.
            Assert.Equal(-1, TeleportContent.DefaultType);
        }

        // ─── What it refuses, and what it does not ───────────────────────────────

        [Fact]
        public void A_passage_that_leads_nowhere_is_a_complaint()
        {
            var wrong = A(toMap: 0);
            Assert.Contains(TeleportContent.Complaints(new[] { wrong }),
                            said => said.Contains("nowhere"));
        }

        [Fact]
        public void A_landing_cell_off_the_map_is_a_complaint()
        {
            Assert.NotEmpty(TeleportContent.Complaints(new[] { A(toCell: 9000) }));
            Assert.NotEmpty(TeleportContent.Complaints(new[] { A(toCell: -1) }));
        }

        /// <summary>
        /// Both ends on the same map is legitimate, and there are 12 of them in the extracted set.
        /// An editor that complained about it would be refusing real content for looking odd.
        /// </summary>
        [Fact]
        public void A_passage_within_one_map_is_not_a_complaint()
            => Assert.Empty(TeleportContent.Complaints(new[] { A(100, 5, 100, 300) }));

        [Fact]
        public void A_good_passage_draws_no_complaint()
            => Assert.Empty(TeleportContent.Complaints(new[] { A() }));

        // ─── Not being there ─────────────────────────────────────────────────────

        [Fact]
        public void A_missing_file_loads_as_nothing()
            => Assert.Equal(0, TeleportContent.Load(Path.Combine(Path.GetTempPath(), "no-such.json")).Count);

        [Fact]
        public void A_file_that_is_not_json_is_reported_and_not_thrown()
        {
            File.WriteAllText(_path, "{ this is not json");

            string complaint = "";
            var store = TeleportContent.Load(_path, said => complaint = said);

            Assert.Equal(0, store.Count);
            Assert.NotEqual("", complaint);
        }

        [Fact]
        public void The_file_it_writes_can_be_read_by_a_person()
        {
            TeleportContent.Save(_path, new[] { A() }, new[] { new PassageKey(7, 8) });

            string written = File.ReadAllText(_path);
            Assert.Contains("_comment", written);
            Assert.Contains("\"toMap\": 200", written);
            Assert.Contains("\"remove\": true", written);
        }
    }
}
