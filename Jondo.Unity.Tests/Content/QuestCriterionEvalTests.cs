using System.Collections.Generic;
using Jondo.Unity.World.Quests;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// Judging a quest's start condition.
    /// </summary>
    /// <remarks>
    /// This decides whether an NPC offers a quest at all, so every mistake in it is a quest that
    /// either never appears or appears to someone who has not earned it — and neither shows up as
    /// an error anywhere. The cases below are the real grammar, measured over all 1,976 conditions,
    /// and several of them are strings taken straight out of the catalogue.
    /// </remarks>
    public class QuestCriterionEvalTests
    {
        /// <summary>A character the tests can describe in one line.</summary>
        private sealed class Facts : IQuestFacts
        {
            public int Level { get; set; } = 1;
            public long MapId { get; set; }
            public HashSet<int> Done { get; } = new HashSet<int>();
            public HashSet<int> Doing { get; } = new HashSet<int>();
            public HashSet<int> Ticked { get; } = new HashSet<int>();

            public bool Finished(int questId) => Done.Contains(questId);
            public bool Active(int questId) => Doing.Contains(questId);
            public bool ObjectiveDone(int objectiveId) => Ticked.Contains(objectiveId);
        }

        private static CriterionVerdict Judge(string criterion, Facts facts)
            => QuestCriterion.Judge(criterion, facts);

        // ─── The simple shapes ────────────────────────────────────────────────────

        [Fact]
        public void An_empty_condition_is_met()
        {
            var verdict = Judge("", new Facts());
            Assert.True(verdict.Met);
            Assert.True(verdict.FullyJudged);
        }

        [Fact]
        public void Level_is_strictly_greater()
        {
            // PL>29 is level 30 and up. Reading it as "at least 29" would let a level 29 character
            // take a quest the real server refuses, and nobody would ever notice.
            Assert.False(Judge("PL>29", new Facts { Level = 29 }).Met);
            Assert.True(Judge("PL>29", new Facts { Level = 30 }).Met);
        }

        [Fact]
        public void Level_below_works_too()
        {
            Assert.True(Judge("PL<51", new Facts { Level = 50 }).Met);
            Assert.False(Judge("PL<51", new Facts { Level = 51 }).Met);
        }

        [Fact]
        public void A_finished_quest_is_a_prerequisite()
        {
            var facts = new Facts();
            Assert.False(Judge("Qf=55", facts).Met);

            facts.Done.Add(55);
            Assert.True(Judge("Qf=55", facts).Met);
        }

        [Fact]
        public void The_bang_is_not_equal_all_by_itself()
        {
            // Qa!496 is "quest 496 is NOT active". There is no != anywhere in the catalogue, and
            // treating the ! as noise would invert 236 conditions.
            var facts = new Facts();
            Assert.True(Judge("Qa!496", facts).Met);

            facts.Doing.Add(496);
            Assert.False(Judge("Qa!496", facts).Met);
        }

        [Fact]
        public void Qc_is_read_as_finished_like_Qf()
        {
            var facts = new Facts();
            facts.Done.Add(890);
            Assert.True(Judge("Qc=890", facts).Met);
        }

        [Fact]
        public void Qo_asks_whether_an_objective_is_ticked_off()
        {
            var facts = new Facts();
            Assert.False(Judge("Qo>8635", facts).Met);

            facts.Ticked.Add(8635);
            Assert.True(Judge("Qo>8635", facts).Met);
        }

        [Fact]
        public void Pm_is_the_map_being_stood_on()
        {
            Assert.True(Judge("Pm=69207040", new Facts { MapId = 69207040 }).Met);
            Assert.False(Judge("Pm=69207040", new Facts { MapId = 1 }).Met);
        }

        // ─── Joining them up ──────────────────────────────────────────────────────

        [Fact]
        public void And_needs_both()
        {
            var facts = new Facts { Level = 30 };
            Assert.False(Judge("PL>29&Qf=55", facts).Met);

            facts.Done.Add(55);
            Assert.True(Judge("PL>29&Qf=55", facts).Met);
        }

        [Fact]
        public void Or_needs_either()
        {
            var facts = new Facts();
            Assert.False(Judge("Qa=890|Qc=890", facts).Met);

            facts.Doing.Add(890);
            Assert.True(Judge("Qa=890|Qc=890", facts).Met);
        }

        [Fact]
        public void Brackets_group_an_or_inside_an_and()
        {
            // Quest 272's shape, cut down: three alternatives, all of which must be joined to the
            // level test. Read flat, left to right, a passing level would carry the whole thing.
            var facts = new Facts { Level = 130 };
            Assert.False(Judge("PL>129&(Qf=272|Qf=273)", facts).Met);

            facts.Done.Add(273);
            Assert.True(Judge("PL>129&(Qf=272|Qf=273)", facts).Met);
        }

        [Fact]
        public void And_binds_tighter_than_or()
        {
            // Straight out of the catalogue: "(Qa=1523&Qo>8635)|Qf=1523". Both readings agree here
            // because the & is bracketed, which is why the data never depends on precedence — but
            // the reading still has to be the one that agrees.
            var facts = new Facts();
            facts.Done.Add(1523);
            Assert.True(Judge("(Qa=1523&Qo>8635)|Qf=1523", facts).Met);

            var other = new Facts();
            other.Doing.Add(1523);
            Assert.False(Judge("(Qa=1523&Qo>8635)|Qf=1523", other).Met);

            other.Ticked.Add(8635);
            Assert.True(Judge("(Qa=1523&Qo>8635)|Qf=1523", other).Met);
        }

        [Fact]
        public void Brackets_three_deep_are_read()
        {
            // The deepest in the catalogue is quest 704's, which nests three levels.
            var facts = new Facts { Level = 9, MapId = 69207040 };
            facts.Done.Add(715);

            var verdict = Judge("PL>8&((Pm=69207040&(Qc=715|Qo=4594))|(Pm=183765002&(Qc=458|Qo=3147)))",
                                facts);

            Assert.True(verdict.Met);
            Assert.True(verdict.FullyJudged);
        }

        [Fact]
        public void The_wrong_branch_of_a_deep_bracket_does_not_pass()
        {
            var facts = new Facts { Level = 9, MapId = 183765002 };
            facts.Done.Add(715);

            Assert.False(Judge("PL>8&((Pm=69207040&(Qc=715|Qo=4594))|(Pm=183765002&(Qc=458|Qo=3147)))",
                               facts).Met);
        }

        // ─── What it does not understand ──────────────────────────────────────────

        [Fact]
        public void An_operator_it_cannot_judge_is_let_through_and_named()
        {
            // Ad is alignment, which this emulator does not model. Refusing it would make 387
            // quests unobtainable by anybody, which is a worse answer than offering them early.
            var verdict = Judge("Ad=3", new Facts());

            Assert.True(verdict.Met);
            Assert.False(verdict.FullyJudged);
            Assert.Equal(new[] { "Ad=3" }, verdict.Skipped);
        }

        [Fact]
        public void The_terms_it_does_understand_are_still_enforced()
        {
            // The important half. A condition with one unknown term in it must not become a free
            // pass for the rest of it.
            var verdict = Judge("Ad=3&PL>29", new Facts { Level = 10 });

            Assert.False(verdict.Met);
            Assert.Single(verdict.Skipped);
        }

        [Fact]
        public void An_unknown_term_behind_an_or_is_still_reported()
        {
            // The right-hand side is read even when the left has already decided the answer, so
            // that what it skipped is still named. Reporting is the whole point.
            var verdict = Judge("PL>1|Sc=4", new Facts { Level = 50 });

            Assert.True(verdict.Met);
            Assert.Equal(new[] { "Sc=4" }, verdict.Skipped);
        }

        [Fact]
        public void A_list_of_values_is_read_past_rather_than_choking()
        {
            // DD>6,11267 is one of 137 terms with a comma list. Nothing understands DD, but a
            // reader that stopped there would lose everything after it.
            var verdict = Judge("DD>6,11267&PL>29", new Facts { Level = 40 });

            Assert.True(verdict.Met);
            Assert.Single(verdict.Skipped);
        }

        [Fact]
        public void Nonsense_is_reported_rather_than_thrown()
        {
            // The condition comes out of a generated file. Anything that throws here takes the
            // server down at the moment somebody talks to an NPC.
            foreach (string junk in new[] { "Qf", "Qf=", "((PL>2", "&&&", "PL>x" })
            {
                var verdict = Judge(junk, new Facts { Level = 50 });
                Assert.False(verdict.FullyJudged);
            }
        }

        [Fact]
        public void Which_operators_are_understood_is_stated()
        {
            foreach (string known in new[] { "PL", "Qf", "Qa", "Qc", "Qo", "Pm" })
            {
                Assert.True(QuestCriterion.Understands(known));
            }

            foreach (string unknown in new[] { "Ad", "Pa", "Ps", "Sc", "PG", "BT" })
            {
                Assert.False(QuestCriterion.Understands(unknown));
            }
        }
    }
}
