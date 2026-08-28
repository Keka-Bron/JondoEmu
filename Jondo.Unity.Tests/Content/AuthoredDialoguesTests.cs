using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Content;
using Jondo.Unity.World.Quests;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// The dialogue trees that are actually written, checked against the catalogue they serve.
    /// </summary>
    /// <remarks>
    /// These are not tests of the loader — that has its own — but of the <em>content</em>. Most of
    /// the trees in <c>content/npcs/dialogues.json</c> were built by a tool that reads a fan site,
    /// matches French reply text against the client's own table, and guesses the ordering; every
    /// step of that can produce a file that loads perfectly and is wrong.
    ///
    /// What goes wrong looks like nothing. A reply pointing at a line that is not in the tree ends
    /// the conversation instead of continuing it. A <c>startsQuest</c> naming a quest that is not
    /// in the catalogue silently hands out nothing. A tree whose quest line is not the one the step
    /// declares means the quest can be walked to and never given. None of those throw.
    /// </remarks>
    public class AuthoredDialoguesTests
    {
        private static string Path => Paths.ContentFile(NpcDialogueContent.AuthoredFile);

        private static bool Available => File.Exists(Path) && File.Exists(Paths.QuestsJson);

        private static List<NpcDialogue> Trees()
        {
            var store = NpcDialogueContent.Load(Path);
            var trees = new List<NpcDialogue>();
            foreach (var row in store.Rows) trees.Add(row.Value.Value);
            return trees;
        }

        [Fact]
        public void Every_tree_that_is_written_is_coherent()
        {
            if (!Available) return;

            var wrong = new List<string>();
            foreach (var tree in Trees())
            {
                foreach (string complaint in NpcDialogueContent.Complaints(tree))
                {
                    wrong.Add($"npc {tree.NpcId}: {complaint}");
                }
            }

            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }

        [Fact]
        public void A_reply_that_leads_somewhere_leads_to_a_line_that_is_there()
        {
            if (!Available) return;

            // The failure this catches is the quiet one: a `next` pointing at a line the tree does
            // not carry does not error, it just ends the conversation, and the quest behind it can
            // never be reached.
            var wrong = new List<string>();
            foreach (var tree in Trees())
            {
                var known = new HashSet<long>(tree.Lines.Select(l => l.Message));
                foreach (var line in tree.Lines)
                {
                    foreach (var choice in line.Choices)
                    {
                        if (!choice.Ends && !known.Contains(choice.Next))
                        {
                            wrong.Add($"npc {tree.NpcId}: reply {choice.Reply} on line {line.Message} " +
                                      $"leads to {choice.Next}, which is not in the tree");
                        }
                    }
                }
            }

            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }

        [Fact]
        public void Every_quest_a_reply_hands_over_is_a_real_quest_given_by_that_npc()
        {
            if (!Available) return;

            // Two mistakes in one check. A startsQuest naming a quest that does not exist hands out
            // nothing at all; one naming a quest somebody ELSE gives out is a tree built from the
            // wrong page — which is a live risk, because the site was caught serving one quest's
            // walkthrough under another quest's URL.
            //
            // A quest the catalogue names NOBODY for is a third case and it is allowed. There are
            // 155 of them, so the catalogue has no opinion to contradict, and the client's own data
            // often knows better: "Mort au rat !" has no giver, and Grobid the tavern keeper
            // declares the reply "Dire que vous avez vu l'affiche placardée dehors" — which is the
            // poster the quest starts from. Refusing that would throw away the only evidence there
            // is.
            var book = new QuestCatalogue();
            if (!book.Ready) return;

            var wrong = new List<string>();
            foreach (var tree in Trees())
            {
                foreach (var line in tree.Lines)
                {
                    foreach (var choice in line.Choices)
                    {
                        if (choice.StartsQuest == 0) continue;

                        var quest = book.Of(choice.StartsQuest);
                        if (quest == null)
                        {
                            wrong.Add($"npc {tree.NpcId}: reply {choice.Reply} hands over quest " +
                                      $"{choice.StartsQuest}, which is not in the catalogue");
                            continue;
                        }

                        if (quest.Givers.Count == 0) continue;

                        bool his = quest.Givers.Any(g => g.NpcId == tree.NpcId);
                        if (!his)
                        {
                            wrong.Add($"npc {tree.NpcId}: reply {choice.Reply} hands over quest " +
                                      $"{choice.StartsQuest} \"{quest.Name}\", which that NPC does not give");
                        }
                    }
                }
            }

            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }

        [Fact]
        public void The_line_a_quest_is_handed_over_on_is_the_one_its_step_declares()
        {
            if (!Available) return;

            // The whole point of a tree. A quest step names the line it is given on, and a tree
            // that hands the quest over somewhere else means the player reaches a conversation the
            // client renders perfectly and the quest is never the one it looks like.
            var book = new QuestCatalogue();
            if (!book.Ready) return;

            var wrong = new List<string>();
            foreach (var tree in Trees())
            {
                foreach (var line in tree.Lines)
                {
                    foreach (var choice in line.Choices)
                    {
                        if (choice.StartsQuest == 0) continue;

                        var quest = book.Of(choice.StartsQuest);
                        if (quest == null || quest.Steps.Count == 0) continue;

                        long declared = quest.Steps[0].DialogId;
                        if (declared != 0 && declared != line.Message)
                        {
                            wrong.Add($"npc {tree.NpcId}: quest {choice.StartsQuest} is handed over on " +
                                      $"line {line.Message}, but its first step declares {declared}");
                        }
                    }
                }
            }

            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }

        [Fact]
        public void A_tree_that_carries_a_quests_line_marks_a_reply_on_it()
        {
            if (!Available) return;

            // Writing a tree TAKES A QUEST AWAY unless the tree says otherwise, and that is easy to
            // walk into. With nothing written, any reply on the line a step names hands the quest
            // over; the moment a tree exists for that NPC, NpcHandler stops asking the catalogue
            // and only a reply marked startsQuest gives anything. So a tree that carries the line
            // and marks nothing on it leaves a conversation that reads perfectly and hands over
            // nothing — and the NPC used to give the quest before the tree was written.
            var book = new QuestCatalogue();
            if (!book.Ready) return;

            var wrong = new List<string>();
            foreach (var tree in Trees())
            {
                var marked = new HashSet<int>();
                foreach (var line in tree.Lines)
                {
                    foreach (var choice in line.Choices)
                    {
                        if (choice.StartsQuest != 0) marked.Add(choice.StartsQuest);
                    }
                }

                var carried = new HashSet<long>(tree.Lines.Select(l => l.Message));
                foreach (var quest in book.All())
                {
                    if (quest.Steps.Count == 0) continue;
                    if (!quest.Givers.Any(g => g.NpcId == tree.NpcId)) continue;

                    long declared = quest.Steps[0].DialogId;
                    if (declared == 0 || !carried.Contains(declared)) continue;
                    if (marked.Contains(quest.Id)) continue;

                    wrong.Add($"npc {tree.NpcId}: line {declared} is where quest {quest.Id} " +
                              $"\"{quest.Name}\" is handed over, and the tree carries it, but no " +
                              "reply on it says startsQuest — so nobody can take it");
                }
            }

            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }

        [Fact]
        public void A_reply_is_never_both_the_way_in_and_gated_behind_the_quest()
        {
            if (!Available) return;

            // Marking the reply that STARTS a quest as belonging to it would hide it until the
            // quest was under way, and the quest could then never be started by anybody. It is the
            // one combination of the two fields that cannot mean anything.
            foreach (var tree in Trees())
            {
                foreach (var line in tree.Lines)
                {
                    foreach (var choice in line.Choices)
                    {
                        Assert.False(choice.StartsQuest != 0 && choice.Quest == choice.StartsQuest,
                            $"npc {tree.NpcId}: reply {choice.Reply} both starts quest " +
                            $"{choice.StartsQuest} and waits for it");
                    }
                }
            }
        }
    }
}
