using System;
using System.Collections.Generic;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// Which replies a character is actually shown.
    /// </summary>
    /// <remarks>
    /// The bug this exists to stop is one anybody can see by talking to Snori Nairb: he offers all
    /// thirty-nine of his replies at once, including the ones that only make sense halfway through
    /// a quest nobody has started, and the ones that belong to two other conversations entirely.
    ///
    /// The client cannot help — it renders whatever the server sends — so the whole of the rule is
    /// here, and every one of these cases is a way of getting it wrong that would look fine:
    /// showing a quest's replies before it starts, showing a later step's replies at the first
    /// step, and hiding the reply that starts the quest, which would make it unobtainable.
    /// </remarks>
    public class DialogueFilterTests
    {
        private static DialogueLine Line(params DialogueChoice[] choices)
            => new DialogueLine { Message = 100, Choices = choices };

        /// <summary>Nobody has done anything.</summary>
        private static long[] AsNobody(DialogueLine line)
            => line.RepliesFor(_ => false, _ => false, (_, _) => false);

        [Fact]
        public void A_reply_with_no_condition_is_always_offered()
        {
            var line = Line(new DialogueChoice { Reply = 1 });
            Assert.Equal(new long[] { 1 }, AsNobody(line));
        }

        [Fact]
        public void A_reply_that_belongs_to_a_quest_is_hidden_until_it_is_under_way()
        {
            var line = Line(new DialogueChoice { Reply = 1, Quest = 55 });

            Assert.Empty(AsNobody(line));
            Assert.Equal(new long[] { 1 },
                line.RepliesFor(q => q == 55, _ => false, (_, _) => true));
        }

        [Fact]
        public void A_reply_that_belongs_to_a_step_waits_for_that_step()
        {
            // The case that matters most: a quest with five steps whose replies all showed at once
            // would let a player answer the last question before being asked the first.
            var line = Line(new DialogueChoice { Reply = 1, Quest = 55, Step = 200 });

            Assert.Empty(line.RepliesFor(q => q == 55, _ => false, (_, s) => s == 199));
            Assert.Equal(new long[] { 1 },
                line.RepliesFor(q => q == 55, _ => false, (_, s) => s == 200));
        }

        [Fact]
        public void An_after_quest_reply_waits_for_it_to_be_finished()
        {
            var line = Line(new DialogueChoice { Reply = 1, Quest = 55, AfterQuest = true });

            Assert.Empty(line.RepliesFor(q => q == 55, _ => false, (_, _) => true));
            Assert.Equal(new long[] { 1 },
                line.RepliesFor(_ => false, q => q == 55, (_, _) => false));
        }

        [Fact]
        public void The_reply_that_starts_a_quest_is_shown_to_somebody_who_has_not_got_it()
        {
            // The one that would break everything if it were treated like `quest`. Marking the
            // reply that HANDS a quest over as belonging to that quest would hide it until the
            // quest was under way, and the quest could then never be started by anybody.
            var line = Line(new DialogueChoice { Reply = 1, StartsQuest = 55 });

            Assert.Equal(new long[] { 1 }, AsNobody(line));
        }

        [Fact]
        public void The_ones_that_do_not_apply_are_dropped_and_the_rest_keep_their_order()
        {
            var line = Line(
                new DialogueChoice { Reply = 1 },
                new DialogueChoice { Reply = 2, Quest = 55 },
                new DialogueChoice { Reply = 3, Quest = 66, Step = 300 },
                new DialogueChoice { Reply = 4, StartsQuest = 77 },
                new DialogueChoice { Reply = 5 });

            Assert.Equal(new long[] { 1, 4, 5 }, AsNobody(line));

            Assert.Equal(new long[] { 1, 2, 4, 5 },
                line.RepliesFor(q => q == 55, _ => false, (_, _) => false));

            Assert.Equal(new long[] { 1, 2, 3, 4, 5 },
                line.RepliesFor(q => q is 55 or 66, _ => false, (_, s) => s == 300));
        }

        [Fact]
        public void A_line_can_end_up_with_nothing_to_say_back()
        {
            // Allowed now. The client draws its own Leave button and the X closes the window,
            // because the server answers the kla it sends — 192 of those in the captures, and it
            // was not handled at all, which is why a reply-less NPC used to trap the player.
            var line = Line(new DialogueChoice { Reply = 1, Quest = 55 });
            Assert.Empty(AsNobody(line));
        }

        [Fact]
        public void What_a_choice_reads_as_says_what_it_is_for()
        {
            Assert.Equal("1 → 2", new DialogueChoice { Reply = 1, Next = 2 }.ToString());
            Assert.Equal("1 ✕", new DialogueChoice { Reply = 1 }.ToString());
            Assert.Equal("1 ✕ (quest 55)", new DialogueChoice { Reply = 1, Quest = 55 }.ToString());
            Assert.Equal("1 ✕ (quest 55 step 200)",
                new DialogueChoice { Reply = 1, Quest = 55, Step = 200 }.ToString());
        }
    }
}
