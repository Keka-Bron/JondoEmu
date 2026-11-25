using System;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// The map puzzles' pieces: the cases an element or a cell answers with, the marks they leave
    /// for a while, and the floor riddle of the Toxine Gang's prison, read from the shipped content.
    /// </summary>
    public class PuzzleTests
    {
        private const long Islets = 187171330;
        private const long Scrolls = 187172354;
        private const int ScrollsElement = 511765;

        private static PuzzleCase[] Cases(string json)
            => PuzzleCase.Read(JsonDocument.Parse(json).RootElement);

        [Fact]
        public void The_first_case_that_holds_is_played()
        {
            var cases = Cases("""
                {"cases":[
                  {"criterion":"PO=19476","lacks":["watched","padlock"],"message":867,"mark":"padlock"},
                  {"lacks":"padlock","message":866,"mark":"watched","for":120}]}
                """);
            bool carries = false;
            var marks = new System.Collections.Generic.HashSet<string>();
            bool Holds(string criterion) => carries;
            PuzzleCase? Pick() => PuzzleCase.First(cases, Holds, marks.Contains);

            Assert.Equal(new[] { 866 }, Pick()!.Messages);
            carries = true;
            Assert.Equal(new[] { 867 }, Pick()!.Messages);
            marks.Add("watched");
            Assert.Equal(new[] { 866 }, Pick()!.Messages);
            marks.Add("padlock");
            Assert.Null(Pick());
        }

        [Fact]
        public void A_case_reads_its_messages_items_marks_and_move()
        {
            var said = Cases("""
                {"cases":[{"message":[869,868],"mark":"watched","for":120,
                           "takes":[{"item":19472},{"item":19473,"count":2}],"gives":[{"item":19470}],
                           "unmark":["a","b"],"toMap":187304450,"toCell":300}]}
                """).Single();
            Assert.Equal(new[] { 869, 868 }, said.Messages);
            Assert.Equal(new[] { "watched" }, said.Mark);
            Assert.Equal(new[] { "a", "b" }, said.Unmark);
            Assert.Equal(120, said.For);
            Assert.Equal(new[] { (19472, 1), (19473, 2) }, said.Takes);
            Assert.Equal(new[] { (19470, 1) }, said.Gives);
            Assert.Equal(187304450, said.ToMap);
            Assert.Equal(300, said.ToCell);
        }

        [Fact]
        public void A_mark_with_seconds_runs_out_and_one_without_stays()
        {
            const long who = -987654321;
            var now = DateTimeOffset.UnixEpoch;
            PuzzleMarks.Set(who, "watched", 120, now);
            PuzzleMarks.Set(who, "padlock", 0, now);
            Assert.True(PuzzleMarks.Has(who, "watched", now.AddSeconds(119)));
            Assert.False(PuzzleMarks.Has(who, "watched", now.AddSeconds(121)));
            Assert.True(PuzzleMarks.Has(who, "padlock", now.AddDays(1)));
            PuzzleMarks.Clear(who, "padlock");
            Assert.False(PuzzleMarks.Has(who, "padlock", now));
        }

        [Fact]
        public void A_variant_is_the_same_for_a_character_and_never_out_of_range()
        {
            foreach (long who in new long[] { 0, 1, 23, 24, -5, long.MaxValue })
            {
                int variant = PuzzleMarks.Variant(who, 24);
                Assert.InRange(variant, 0, 23);
                Assert.Equal(variant, PuzzleMarks.Variant(who, 24));
            }
        }

        [Fact]
        public void A_digit_is_right_only_at_its_own_step()
        {
            Assert.True(FloorRiddles.Right("245", 0, '2'));
            Assert.True(FloorRiddles.Right("245", 2, '5'));
            Assert.False(FloorRiddles.Right("245", 1, '5'));
            Assert.False(FloorRiddles.Right("245", 3, '5'));
            Assert.False(FloorRiddles.Right("", 0, '2'));
        }

        [Fact]
        public void The_prison_riddle_has_an_answer_on_the_tiles_for_every_scroll()
        {
            string path = Paths.ContentFile(FloorRiddles.File);
            var (spots, tiles) = FloorRiddles.Count(path);
            Assert.True(spots >= 2);
            Assert.Equal(12, tiles);

            var scrolls = Readables.Of(Scrolls, ScrollsElement);
            Assert.NotNull(scrolls);
            Assert.Equal(24, scrolls!.Documents.Length);

            var root = JsonDocument.Parse(System.IO.File.ReadAllText(path)).RootElement;
            var riddle = root.GetProperty("riddles").EnumerateArray().Single(r => r.GetProperty("map").GetInt64() == Islets);
            var answers = riddle.GetProperty("answers");
            var steps = riddle.GetProperty("steps").EnumerateArray()
                              .Select(s => s.GetProperty("tiles").EnumerateObject().Select(t => t.Value.GetRawText()[0]).ToHashSet())
                              .ToArray();
            foreach (int document in scrolls.Documents)
            {
                string answer = answers.GetProperty(document.ToString()).GetString()!;
                Assert.Equal(steps.Length, answer.Length);
                for (int i = 0; i < answer.Length; i++) Assert.Contains(answer[i], steps[i]);
            }
        }

        [Fact]
        public void A_character_always_reads_the_same_scroll()
        {
            var scrolls = Readables.Of(Scrolls, ScrollsElement)!;
            foreach (long who in new long[] { 1, 2, 777, 123456789 })
            {
                int document = scrolls.DocumentFor(who);
                Assert.Contains(document, scrolls.Documents);
                Assert.Equal(document, scrolls.DocumentFor(who));
            }
        }
    }
}
