using Jondo.Unity.Studio.Data;
using Xunit;
using Jondo.Unity.World.Quests;

namespace Jondo.Unity.Tests.Studio
{
    /// <summary>
    /// Reading the prerequisite out of a quest's start condition.
    /// </summary>
    /// <remarks>
    /// Ankama writes the whole condition as one string with 29 different two-letter operators in
    /// it, and exactly one of them says "this other quest must be finished". Getting that wrong is
    /// invisible: the screen would draw an arrow between two quests that have nothing to do with
    /// each other, and it would look perfectly reasonable. 990 of the 1,976 quests have one, so
    /// there is a lot of chain to get wrong.
    /// </remarks>
    public class QuestCriterionTests
    {
        [Fact]
        public void A_plain_requirement_is_read()
        {
            Assert.Equal(new[] { 55 }, QuestCatalogue.FinishedQuests("Qf=55"));
        }

        [Fact]
        public void It_is_found_among_the_other_operators()
        {
            // Straight out of quest 56: a level, two alignment terms, and the prerequisite.
            Assert.Equal(new[] { 55 }, QuestCatalogue.FinishedQuests("Ps=1&Pa=1&PL>29&Qf=55"));
        }

        [Fact]
        public void Several_requirements_all_come_out()
        {
            Assert.Equal(new[] { 55, 61 }, QuestCatalogue.FinishedQuests("Qf=55&PL>29&Qf=61"));
        }

        [Fact]
        public void The_same_quest_twice_is_listed_once()
        {
            Assert.Equal(new[] { 55 }, QuestCatalogue.FinishedQuests("Qf=55|Qf=55"));
        }

        [Fact]
        public void Not_finished_is_not_a_prerequisite()
        {
            // Qf!=55 means the opposite. Reading it as a requirement would draw the arrow the
            // wrong way round, which is worse than drawing no arrow.
            Assert.Empty(QuestCatalogue.FinishedQuests("Qf!=55"));
        }

        [Fact]
        public void A_comparison_that_is_not_equality_is_left_alone()
        {
            Assert.Empty(QuestCatalogue.FinishedQuests("Qf>55"));
            Assert.Empty(QuestCatalogue.FinishedQuests("Qf<55"));
        }

        [Fact]
        public void An_active_quest_is_not_a_finished_one()
        {
            // Qa is "quest active", 813 uses. It is a different condition and must not be read as
            // a prerequisite: a quest you are in the middle of has not been completed.
            Assert.Empty(QuestCatalogue.FinishedQuests("Qa=55"));
        }

        [Fact]
        public void Other_operators_holding_numbers_are_not_mistaken_for_it()
        {
            Assert.Empty(QuestCatalogue.FinishedQuests("PL>109&Ad=3&Pr=2&BT=1"));
        }

        [Fact]
        public void An_empty_or_junk_criterion_gives_nothing_and_does_not_throw()
        {
            Assert.Empty(QuestCatalogue.FinishedQuests(""));
            Assert.Empty(QuestCatalogue.FinishedQuests("Qf"));
            Assert.Empty(QuestCatalogue.FinishedQuests("Qf="));
            Assert.Empty(QuestCatalogue.FinishedQuests("Qf=&PL>2"));
        }

        // ─── Which objective types name an NPC ────────────────────────────────────

        [Theory]
        [InlineData(1)]   // go and see #1
        [InlineData(2)]   // show #1
        [InlineData(3)]   // hand to #1
        [InlineData(9)]   // go back and see #1
        [InlineData(12)]  // take souls to #1
        public void The_five_npc_types_are_recognised(int type)
        {
            Assert.True(QuestCatalogue.NamesAnNpc(type));
        }

        [Theory]
        [InlineData(0)]   // free text
        [InlineData(4)]   // discover map #1
        [InlineData(6)]   // beat #2 x #1 in one fight
        [InlineData(14)]  // beat #2 x #1
        [InlineData(17)]  // craft #2 #1
        public void The_others_are_not(int type)
        {
            // parameter0 on these is a map, a monster or an item. Reading it as an NPC id would
            // put somebody's name where a monster belongs, and it would look plausible.
            Assert.False(QuestCatalogue.NamesAnNpc(type));
        }
    }
}
