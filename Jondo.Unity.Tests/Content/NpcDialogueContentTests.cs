using System;
using System.Collections.Generic;
using System.IO;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// The dialogue trees: which reply leads to which line.
    /// </summary>
    /// <remarks>
    /// This is the one piece of NPC data that cannot be checked against anything. Every other
    /// number in this project can be measured off a capture or read out of the client dump; this
    /// pairing exists nowhere but in what somebody decided, so the file is the only copy and a save
    /// that quietly drops a branch loses work nothing can rebuild.
    ///
    /// And a tree that is wrong fails in the nastiest way there is: a reply pointing at a line that
    /// is not there leaves the player looking at a window with no button that answers, on somebody
    /// else's machine, days later.
    /// </remarks>
    public class NpcDialogueContentTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(),
                                                     "jondo-dialogues-" + Guid.NewGuid().ToString("N") + ".json");

        public void Dispose()
        {
            try { File.Delete(_path); } catch (IOException) { }
        }

        private static DialogueChoice Choice(long reply, long next = 0)
            => new DialogueChoice { Reply = reply, Next = next };

        private static DialogueLine Line(long message, params DialogueChoice[] choices)
            => new DialogueLine { Message = message, Choices = choices };

        private static NpcDialogue Tree(int npc, long opening, params DialogueLine[] lines)
            => new NpcDialogue { NpcId = npc, MapId = 0, Opening = opening, Lines = lines };

        // ─── Round trip ───────────────────────────────────────────────────────────

        [Fact]
        public void A_tree_survives_being_written_and_read()
        {
            NpcDialogueContent.Save(_path, new[]
            {
                Tree(1088, 3312,
                     Line(3312, Choice(6016, 6047), Choice(7846)),
                     Line(6047, Choice(7846))),
            });

            var store = NpcDialogueContent.Load(_path);
            var tree = NpcDialogueContent.For(store, 1088, 241438721);

            Assert.NotNull(tree);
            Assert.Equal(3312, tree!.Opening);
            Assert.Equal(2, tree.Lines.Count);

            var first = tree.Line(3312);
            Assert.NotNull(first);
            Assert.Equal(2, first!.Choices.Count);
            Assert.Equal(6047, first.Choice(6016)!.Next);
            Assert.True(first.Choice(7846)!.Ends);
        }

        [Fact]
        public void A_reply_that_ends_the_conversation_writes_no_destination()
        {
            NpcDialogueContent.Save(_path, new[] { Tree(1, 10, Line(10, Choice(20))) });

            // The key, not the word: the file's own header explains what "next" means, so looking
            // for the bare word finds the documentation and passes for the wrong reason.
            Assert.DoesNotContain("\"next\":", File.ReadAllText(_path));
        }

        /// <summary>
        /// The opening line matters and is not the same as the first one in the list: an editor
        /// that adds lines in the order somebody clicked them would otherwise silently reorder the
        /// conversation.
        /// </summary>
        [Fact]
        public void The_opening_line_is_kept_even_when_it_is_not_the_first()
        {
            NpcDialogueContent.Save(_path, new[]
            {
                Tree(1, 20, Line(10, Choice(1)), Line(20, Choice(2))),
            });

            var tree = NpcDialogueContent.For(NpcDialogueContent.Load(_path), 1, 0);

            Assert.Equal(20, tree!.Opening);
            Assert.Equal(20, tree.First()!.Message);
        }

        [Fact]
        public void With_no_opening_written_it_starts_on_the_first_line()
        {
            var tree = new NpcDialogue
            {
                NpcId = 1,
                Lines = new[] { Line(10, Choice(1)), Line(20, Choice(2)) },
            };

            Assert.Equal(10, tree.First()!.Message);
        }

        // ─── One NPC, two places ──────────────────────────────────────────────────

        /// <summary>
        /// The opening line is per map — the same character in two places has no reason to say the
        /// same thing, and the real game does not make it. A conversation written for a map beats
        /// the one written for everywhere.
        /// </summary>
        [Fact]
        public void A_dialogue_for_one_map_beats_the_one_for_all_of_them()
        {
            NpcDialogueContent.Save(_path, new[]
            {
                Tree(1088, 10, Line(10, Choice(1))),
                new NpcDialogue
                {
                    NpcId = 1088, MapId = 241438721, Opening = 99,
                    Lines = new[] { Line(99, Choice(2)) },
                },
            });

            var store = NpcDialogueContent.Load(_path);

            Assert.Equal(99, NpcDialogueContent.For(store, 1088, 241438721)!.Opening);
            Assert.Equal(10, NpcDialogueContent.For(store, 1088, 555)!.Opening);
        }

        [Fact]
        public void An_npc_with_nothing_written_has_no_dialogue()
            => Assert.Null(NpcDialogueContent.For(NpcDialogueContent.Load(_path), 4242, 0));

        // ─── What the file has to survive ─────────────────────────────────────────

        [Fact]
        public void The_order_does_not_depend_on_the_order_they_were_added()
        {
            var one = new[] { Tree(30, 1, Line(1, Choice(9))), Tree(10, 1, Line(1, Choice(9))) };
            var other = new[] { Tree(10, 1, Line(1, Choice(9))), Tree(30, 1, Line(1, Choice(9))) };

            NpcDialogueContent.Save(_path, one);
            string first = File.ReadAllText(_path);
            NpcDialogueContent.Save(_path, other);

            Assert.Equal(first, File.ReadAllText(_path));
        }

        [Fact]
        public void Saving_leaves_no_half_written_file_behind()
        {
            NpcDialogueContent.Save(_path, new[] { Tree(1, 10, Line(10, Choice(1))) });

            Assert.True(File.Exists(_path));
            Assert.False(File.Exists(_path + ".writing"));
        }

        [Fact]
        public void A_broken_file_is_reported_rather_than_thrown()
        {
            File.WriteAllText(_path, "{ not json at all");

            string complaint = "";
            var store = NpcDialogueContent.Load(_path, message => complaint = message);

            Assert.Equal(0, store.Count);
            Assert.Contains("unreadable", complaint);
        }

        [Fact]
        public void A_line_with_no_message_is_skipped_and_said_out_loud()
        {
            File.WriteAllText(_path,
                "{ \"dialogues\": [ { \"npc\": 1, \"lines\": [ { \"choices\": [] }, " +
                "{ \"message\": 10, \"choices\": [ { \"reply\": 5 } ] } ] } ] }");

            var complaints = new List<string>();
            var store = NpcDialogueContent.Load(_path, complaints.Add);

            var tree = NpcDialogueContent.For(store, 1, 0);
            Assert.Single(tree!.Lines);
            Assert.Single(complaints);
        }

        [Fact]
        public void A_removed_dialogue_stays_removed()
        {
            File.WriteAllText(_path, "{ \"dialogues\": [ { \"npc\": 1088, \"remove\": true } ] }");

            var store = NpcDialogueContent.Load(_path);

            Assert.Equal(0, store.Count);
            Assert.Equal(1, store.ErasedCount);
        }

        // ─── Complaining before it reaches a player ───────────────────────────────

        [Fact]
        public void A_correct_tree_has_nothing_wrong_with_it()
        {
            var wrong = NpcDialogueContent.Complaints(
                Tree(1, 10, Line(10, Choice(1, 20)), Line(20, Choice(2))));

            Assert.Empty(wrong);
        }

        /// <summary>
        /// The one that matters. A reply aiming at a line that is not there leaves the player
        /// looking at a window that never closes.
        /// </summary>
        [Fact]
        public void A_reply_leading_nowhere_is_caught()
        {
            var wrong = NpcDialogueContent.Complaints(Tree(1, 10, Line(10, Choice(1, 999))));

            Assert.Contains(wrong, complaint => complaint.Contains("999"));
        }

        /// <summary>
        /// With an empty reply list the client draws its own Leave button, and that button does not
        /// answer back: the window stays up and there is no way out but reconnecting. It is what
        /// the Bontarian guard does, with one message and zero replies in his template.
        /// </summary>
        [Fact]
        public void A_line_with_no_replies_is_caught()
        {
            var wrong = NpcDialogueContent.Complaints(Tree(1, 10, Line(10)));

            Assert.Contains(wrong, complaint => complaint.Contains("close the window"));
        }

        [Fact]
        public void Opening_on_a_line_that_is_not_there_is_caught()
        {
            var wrong = NpcDialogueContent.Complaints(Tree(1, 77, Line(10, Choice(1))));

            Assert.Contains(wrong, complaint => complaint.Contains("77"));
        }

        [Fact]
        public void The_same_message_twice_is_caught()
        {
            var wrong = NpcDialogueContent.Complaints(
                Tree(1, 10, Line(10, Choice(1)), Line(10, Choice(2))));

            Assert.Contains(wrong, complaint => complaint.Contains("twice"));
        }

        [Fact]
        public void The_same_reply_twice_under_one_line_is_caught()
        {
            var wrong = NpcDialogueContent.Complaints(Tree(1, 10, Line(10, Choice(1), Choice(1))));

            Assert.Contains(wrong, complaint => complaint.Contains("twice"));
        }

        /// <summary>
        /// A conversation that loops back on itself is fine and is not a complaint: walking away is
        /// always possible because every line has to offer a reply that ends it.
        /// </summary>
        [Fact]
        public void A_loop_is_allowed()
        {
            var wrong = NpcDialogueContent.Complaints(
                Tree(1, 10,
                     Line(10, Choice(1, 20), Choice(9)),
                     Line(20, Choice(2, 10), Choice(9))));

            Assert.Empty(wrong);
        }
    }
}
