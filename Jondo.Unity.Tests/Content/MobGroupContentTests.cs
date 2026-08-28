using System;
using System.Collections.Generic;
using System.IO;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// Monster groups put somewhere on purpose, and Ankama's own taken away.
    /// </summary>
    public class MobGroupContentTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(),
                                                     "jondo-groups-" + Guid.NewGuid().ToString("N") + ".json");

        public void Dispose()
        {
            try { File.Delete(_path); } catch (IOException) { }
        }

        private static MobGroupSpawn Group(long map, long id, int cell, params (int Monster, int Grade)[] members)
        {
            var party = new List<MobMemberSpec>();
            foreach (var (monster, grade) in members)
            {
                party.Add(new MobMemberSpec { MonsterId = monster, Grade = grade });
            }

            return new MobGroupSpawn { MapId = map, GroupId = id, Cell = cell, Members = party };
        }

        // ─── Round trip ───────────────────────────────────────────────────────────

        [Fact]
        public void A_group_survives_being_written_and_read()
        {
            MobGroupContent.Save(_path,
                new[] { Group(241438721, -2000000, 327, (2549, 0), (263, 3)) },
                Array.Empty<MobGroupKey>());

            var store = MobGroupContent.Load(_path);

            Assert.True(store.TryGet(new MobGroupKey(241438721, -2000000), out var row));
            Assert.Equal(327, row.Value.Cell);
            Assert.Equal(2, row.Value.Members.Count);
            Assert.Equal(263, row.Value.Members[1].MonsterId);
            Assert.Equal(3, row.Value.Members[1].Grade);
            Assert.Equal(ContentLayer.Authored, row.From.Layer);
        }

        [Fact]
        public void A_group_that_was_taken_away_reads_back_as_a_tombstone()
        {
            MobGroupContent.Save(_path, Array.Empty<MobGroupSpawn>(),
                                 new[] { new MobGroupKey(5, -1000000) });

            var store = MobGroupContent.Load(_path);

            Assert.Equal(0, store.Count);
            Assert.Equal(1, store.ErasedCount);
            Assert.Contains(new MobGroupKey(5, -1000000), store.ErasedKeys);
        }

        /// <summary>
        /// The level follows from the monster and the grade, and the server works it out for
        /// Ankama's groups the same way. Writing it down would be a second copy of a derived
        /// number, stale the day a grade table changes.
        /// </summary>
        [Fact]
        public void The_level_is_not_written_down()
        {
            MobGroupContent.Save(_path, new[] { Group(1, -2000000, 10, (31, 0)) },
                                 Array.Empty<MobGroupKey>());

            Assert.DoesNotContain("\"level\"", File.ReadAllText(_path));
        }

        // ─── Ids ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// The measured groups run from -1,000,000 to -1,038,743 and the runtime hands out more
        /// from the same band. Starting at -2,000,000 leaves room for nearly a million more of
        /// Ankama's before the two bands could ever meet.
        /// </summary>
        [Fact]
        public void The_first_id_is_well_clear_of_the_measured_band()
        {
            long first = MobGroupContent.NextId(Array.Empty<MobGroupSpawn>());

            Assert.Equal(MobGroupContent.FirstAuthoredId, first);
            Assert.True(first < -1_038_743, "an authored id could collide with a measured one");
        }

        [Fact]
        public void Ids_do_not_repeat_even_across_maps()
        {
            var already = new List<MobGroupSpawn> { Group(1, MobGroupContent.FirstAuthoredId, 10, (1, 0)) };
            long second = MobGroupContent.NextId(already);

            Assert.Equal(MobGroupContent.FirstAuthoredId - 1, second);

            already.Add(Group(2, second, 20, (1, 0)));
            Assert.Equal(MobGroupContent.FirstAuthoredId - 2, MobGroupContent.NextId(already));
        }

        // ─── Not falling over ─────────────────────────────────────────────────────

        [Fact]
        public void A_group_with_no_monsters_is_skipped_and_said_out_loud()
        {
            File.WriteAllText(_path,
                "{ \"groups\": [ { \"map\": 1, \"group\": -2000000, \"cell\": 5, \"members\": [] } ] }");

            var complaints = new List<string>();
            var store = MobGroupContent.Load(_path, complaints.Add);

            Assert.Equal(0, store.Count);
            Assert.Single(complaints);
        }

        /// <summary>
        /// The client accepts five grades and no more; the database ships some that are higher, and
        /// the server already clamps them when it reads its own. The same has to happen here or a
        /// hand-typed grade would be the one thing that gets through.
        /// </summary>
        [Fact]
        public void A_grade_past_what_the_client_accepts_is_brought_back()
        {
            File.WriteAllText(_path,
                "{ \"groups\": [ { \"map\": 1, \"group\": -2000000, \"cell\": 5, " +
                "\"members\": [ { \"monster\": 31, \"grade\": 9 } ] } ] }");

            var store = MobGroupContent.Load(_path);

            Assert.True(store.TryGet(new MobGroupKey(1, -2000000), out var row));
            Assert.Equal(MobGroupContent.MaxGrade, row.Value.Members[0].Grade);
        }

        [Fact]
        public void A_broken_file_is_reported_rather_than_thrown()
        {
            File.WriteAllText(_path, "{ not json");

            string complaint = "";
            var store = MobGroupContent.Load(_path, message => complaint = message);

            Assert.Equal(0, store.Count);
            Assert.Contains("unreadable", complaint);
        }

        [Fact]
        public void A_file_that_is_not_there_loads_as_nothing()
            => Assert.Equal(0, MobGroupContent.Load(Path.Combine(Path.GetTempPath(), "nope.json")).Count);

        [Fact]
        public void The_order_does_not_depend_on_the_order_they_were_added()
        {
            var one = new[] { Group(30, -2000001, 5, (1, 0)), Group(10, -2000000, 6, (1, 0)) };
            var other = new[] { Group(10, -2000000, 6, (1, 0)), Group(30, -2000001, 5, (1, 0)) };

            MobGroupContent.Save(_path, one, Array.Empty<MobGroupKey>());
            string first = File.ReadAllText(_path);
            MobGroupContent.Save(_path, other, Array.Empty<MobGroupKey>());

            Assert.Equal(first, File.ReadAllText(_path));
        }

        [Fact]
        public void Saving_leaves_no_half_written_file_behind()
        {
            MobGroupContent.Save(_path, new[] { Group(1, -2000000, 5, (1, 0)) },
                                 Array.Empty<MobGroupKey>());

            Assert.True(File.Exists(_path));
            Assert.False(File.Exists(_path + ".writing"));
        }

        [Fact]
        public void Placing_and_taking_away_survive_the_same_file()
        {
            MobGroupContent.Save(_path,
                new[] { Group(1, -2000000, 5, (31, 0)) },
                new[] { new MobGroupKey(1, -1000000) });

            var store = MobGroupContent.Load(_path);

            Assert.Equal(1, store.Count);
            Assert.Equal(1, store.ErasedCount);
        }
    }
}
