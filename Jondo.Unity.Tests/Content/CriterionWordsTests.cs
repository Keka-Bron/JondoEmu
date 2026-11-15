using System;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Quests;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// The terms whose value is a word, and the client's «~»: what keeps the game masters' items
    /// and the named ones for their owners.
    /// </summary>
    /// <remarks>
    /// The Anillote del Maestro Jambo asks «PX=3|PX=5», the account's rights; the Amuleto de Jiva
    /// «PN~Silaisie&amp;BI=1», one character's name and an unusable item; the old John's hat
    /// «(Sc=968&amp;SG=08&amp;Sd&gt;18&amp;Sd&lt;27)|PX=A», its week or an administrator. The «~» broke the
    /// whole condition and a broken one is let through; a word was never asked of anybody.
    /// </remarks>
    public class CriterionWordsTests
    {
        /// <summary>A player as an item's criterion asks about him: rights 0, a name, nothing unusable.</summary>
        private sealed class Player : IQuestFacts
        {
            public string Name { get; init; } = "Somebody";
            public bool Administrator { get; init; }

            public int Level => 5;
            public long MapId => 0;
            public bool Finished(int questId) => false;
            public bool Active(int questId) => false;
            public bool ObjectiveDone(int objectiveId) => false;

            public long? Scalar(string op) => op switch
            {
                "PX" => Administrator ? null : 0,
                "BI" => 0,
                _ => null,
            };

            public bool? Text(string op, char comparison, string value) => op switch
            {
                "PX" when value == "A" => comparison == '!' ? !Administrator : Administrator,
                "PN" => (comparison == '!') ^ string.Equals(Name, value, StringComparison.OrdinalIgnoreCase),
                _ => null,
            };
        }

        [Fact]
        public void A_player_does_not_have_a_game_master_s_rights()
        {
            var verdict = QuestCriterion.Judge("PX=3|PX=5", new Player());
            Assert.False(verdict.Met);
            Assert.True(verdict.FullyJudged);
        }

        [Fact]
        public void An_administrator_s_rights_are_left_to_him()
            => Assert.True(QuestCriterion.Judge("PX=3|PX=5", new Player { Administrator = true }).Met);

        [Fact]
        public void The_tilde_is_read_and_an_unusable_item_is_unusable()
        {
            var verdict = QuestCriterion.Judge("PN~Silaisie&BI=1", new Player { Name = "silaisie" });
            Assert.False(verdict.Broke);
            Assert.False(verdict.Met);      // the right name, but BI=1 is never met

            Assert.True(QuestCriterion.Judge("PN~Silaisie", new Player { Name = "SILAISIE" }).Met);
            Assert.False(QuestCriterion.Judge("PN~Silaisie", new Player { Name = "Another" }).Met);
        }

        [Fact]
        public void A_word_nobody_answers_is_let_through_and_named()
        {
            var verdict = QuestCriterion.Judge("PN~Silaisie", new NobodyKnows());
            Assert.True(verdict.Met);
            Assert.False(verdict.Broke);
            Assert.Contains("PN~Silaisie", verdict.Skipped);
        }

        [Fact]
        public void The_old_john_s_hat_is_an_administrator_s_out_of_its_week()
        {
            Assert.False(QuestCriterion.Judge("(Sc=968&SG=08&Sd>18&Sd<27)|PX=A", new DateFacts(month: 11, day: 15)).Met);
            Assert.True(QuestCriterion.Judge("(Sc=968&SG=08&Sd>18&Sd<27)|PX=A", new DateFacts(month: 11, day: 15, administrator: true)).Met);
            Assert.True(QuestCriterion.Judge("(Sc=968&SG=08&Sd>18&Sd<27)|PX=A", new DateFacts(month: 8, day: 20)).Met);
        }

        [Fact]
        public void The_terms_read_the_tilde_as_equal()
        {
            var terms = QuestCriterion.Terms("PN~Silaisie&BI=1");
            Assert.Equal(2, terms.Count);
            Assert.Equal("PN", terms[0].Op);
            Assert.Equal('=', terms[0].Comparison);
            Assert.Equal("Silaisie", terms[0].Values[0]);
        }

        [Theory]
        [InlineData("PX=3|PX=5", true)]
        [InlineData("PN~Silaisie&BI=1", true)]
        [InlineData("PZ=1&Pn!8&(Pm=74186754|Pm=74187778)&BI=1", true)]
        [InlineData("(Sc=968&SG=08&Sd>18&Sd<27)|PX=A", true)]
        [InlineData("Px>2", false)]                 // a guild's rights, not the account's
        [InlineData("Ps=1&Pa>20", false)]
        [InlineData("CS>99&CA>99&CV>99", false)]
        [InlineData("", false)]
        public void Some_criteria_keep_an_item_for_somebody_in_particular(string criterion, bool reserved)
            => Assert.Equal(reserved, ItemCriteria.IsReserved(criterion));

        private sealed class NobodyKnows : IQuestFacts
        {
            public int Level => 1;
            public long MapId => 0;
            public bool Finished(int questId) => false;
            public bool Active(int questId) => false;
            public bool ObjectiveDone(int objectiveId) => false;
        }

        /// <summary>A day of the year, and a yearly event that is not on.</summary>
        private sealed class DateFacts : IQuestFacts
        {
            private readonly int _month, _day;
            private readonly bool _administrator;

            public DateFacts(int month, int day, bool administrator = false)
                => (_month, _day, _administrator) = (month, day, administrator);

            public int Level => 1;
            public long MapId => 0;
            public bool Finished(int questId) => false;
            public bool Active(int questId) => false;
            public bool ObjectiveDone(int objectiveId) => false;

            public long? Scalar(string op) => op switch { "SG" => _month, "Sd" => _day, _ => null };
            public long? Count(string op, long key, string flag) => op == "Sc" ? (_month == 8 ? 1 : 0) : null;
            public bool? Text(string op, char comparison, string value)
                => op == "PX" && value == "A" ? (comparison == '!') ^ _administrator : null;
        }
    }
}
