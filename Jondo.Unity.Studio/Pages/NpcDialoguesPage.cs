using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Jondo.Unity.Launcher;
using Jondo.Unity.Studio.Data;
using Jondo.Unity.Studio.Ui;
using Jondo.Unity.World.Content;

namespace Jondo.Unity.Studio.Pages
{
    /// <summary>
    /// Which reply leads to which line: the only place that decision can be made.
    /// </summary>
    /// <remarks>
    /// The client ships every line an NPC can say and every reply it can be given, and nowhere does
    /// it say which goes with which — measured across all 6,467 of them. That pairing has always
    /// lived on Ankama's server, so it cannot be extracted, measured, or asked of dofusdude, which
    /// reads the same client. It has to be decided by a person, and this is the only screen in the
    /// project where that is possible.
    ///
    /// The first version of this screen showed an empty box until you added something to it, which
    /// was exactly backwards: what an NPC <em>can</em> say is known, it is right there in the
    /// template, and hiding it behind a drop-down made the screen look broken. So both lists are
    /// always full — every line the NPC has and every reply it has — and the editing is about
    /// which of them are in the tree and what they lead to.
    ///
    /// One thing that cannot be done, and it is worth being plain about: <b>a reply's words cannot
    /// be typed.</b> The client draws them from its own catalogue by id, so an invented text would
    /// come out blank in the game. What can be done instead is pick a line that exists somewhere
    /// else in the game — which is what <see cref="NpcCatalogue.EveryReply"/> is for, and in
    /// practice covers it: "Sí.", "No, gracias." and "Cuéntame más." are all in there already.
    /// </remarks>
    public sealed class NpcDialoguesPage : IStudioPage
    {
        private readonly WorldData _world;
        private NpcCatalogue? _catalogue;

        private readonly Dictionary<NpcDialogueKey, Draft> _drafts = new Dictionary<NpcDialogueKey, Draft>();

        private bool _loaded;
        private bool _dirty;

        public NpcDialoguesPage(WorldData world) => _world = world;

        public string TitleKey => "nav.dialogues";

        public override string ToString() => Words.T(TitleKey);

        // ─── What is being edited ─────────────────────────────────────────────────

        private sealed class Draft
        {
            public int NpcId;
            public long Opening;

            /// <summary>message → the replies hanging under it, and where each leads.</summary>
            public readonly Dictionary<long, List<DraftChoice>> Lines = new Dictionary<long, List<DraftChoice>>();

            public bool Has(long message) => Lines.ContainsKey(message);

            public List<DraftChoice> Under(long message)
                => Lines.TryGetValue(message, out var choices) ? choices : new List<DraftChoice>();

            public bool Empty => Lines.Count == 0;

            public NpcDialogue Freeze()
            {
                var lines = new List<DialogueLine>();
                foreach (var pair in Lines)
                {
                    lines.Add(new DialogueLine
                    {
                        Message = pair.Key,
                        Choices = pair.Value.ConvertAll(c => new DialogueChoice
                        {
                            Reply = c.Reply,
                            Next = c.Next,
                            Quest = c.Quest,
                            Step = c.Step,
                            StartsQuest = c.StartsQuest,
                            AfterQuest = c.AfterQuest,
                        }).ToArray(),
                    });
                }

                // The opening line first, so the file reads in the order the conversation happens.
                lines.Sort((a, b) =>
                {
                    if (a.Message == Opening) return -1;
                    if (b.Message == Opening) return 1;
                    return a.Message.CompareTo(b.Message);
                });

                return new NpcDialogue
                {
                    NpcId = NpcId,
                    MapId = NpcDialogueKey.AnyMap,
                    Opening = Opening != 0 ? Opening : (lines.Count > 0 ? lines[0].Message : 0),
                    Lines = lines,
                };
            }
        }

        private sealed class DraftChoice
        {
            public long Reply;
            public long Next;

            /// <summary>
            /// What the reply is for, carried through untouched.
            /// </summary>
            /// <remarks>
            /// The editor cannot set these yet — a reply's quest and step are written by hand or by
            /// tools/build_dialogue_trees.py — and that is exactly why they are here. Without them
            /// the draft would rebuild every choice from just the reply and the line it leads to,
            /// so opening a conversation in the editor and saving it would quietly erase the quest
            /// markings on every reply of that NPC. Nothing would look wrong until a quest stopped
            /// being handed out.
            /// </remarks>
            public int Quest;
            public int Step;
            public int StartsQuest;
            public bool AfterQuest;
        }

        /// <summary>One line of the NPC, as the list shows it.</summary>
        private sealed record LineRow(long Message, string Text, bool InTree, bool Opens);

        /// <summary>One reply, as the list shows it.</summary>
        private sealed record ReplyRow(long Reply, string Text, bool InLine, long Next, string NextText);

        public Control Build()
        {
            if (!_loaded) Load();

            if (_catalogue == null || !_catalogue.Ready)
            {
                return Missing();
            }

            var npcs = _catalogue.WithDialogue();

            // ─── who ──────────────────────────────────────────────────────────────
            var npcList = new ListBox
            {
                ItemTemplate = new FuncDataTemplate<NpcSummary>((npc, _) => NpcLine(npc, HasTree(npc.Id)),
                                                               supportsRecycling: true),
            };

            var search = new TextBox { Watermark = Words.T("common.search"), FontSize = 12.5 };
            var counts = new TextBlock { Foreground = Skin.TextSoftBrush, VerticalAlignment = VerticalAlignment.Center };
            var save = new Button { Content = Words.T("dlg.save"), IsEnabled = _dirty };

            // ─── what it says ─────────────────────────────────────────────────────
            var lineList = new ListBox
            {
                ItemTemplate = new FuncDataTemplate<LineRow>((row, _) => Line(row), supportsRecycling: true),
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(lineList, ScrollBarVisibility.Disabled);

            var addLine = new Button { Content = Words.T("dlg.addLine"), IsEnabled = false };
            var dropLine = new Button { Content = Words.T("dlg.dropLine"), IsEnabled = false };
            var openHere = new Button { Content = Words.T("dlg.startHere"), IsEnabled = false };

            // ─── what can be said back ────────────────────────────────────────────
            var replyList = new ListBox
            {
                ItemTemplate = new FuncDataTemplate<ReplyRow>((row, _) => Reply(row), supportsRecycling: true),
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(replyList, ScrollBarVisibility.Disabled);

            var replyHint = new TextBlock
            {
                Foreground = Skin.TextSoftBrush,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            };

            var addReply = new Button { Content = Words.T("dlg.addLine"), IsEnabled = false };
            var dropReply = new Button { Content = Words.T("dlg.dropLine"), IsEnabled = false };
            var leadsTo = new ComboBox { Width = 260, PlaceholderText = Words.T("dlg.leadsTo"), IsEnabled = false };

            var tree = new SelectableTextBlock
            {
                FontFamily = Skin.Mono,
                FontSize = 12.5,
                Foreground = Skin.TextBrush,
                TextWrapping = TextWrapping.Wrap,
            };

            var complaints = new TextBlock
            {
                Foreground = Skin.WrongBrush,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
            };

            NpcDialogueSource? source = null;
            Draft? draft = null;
            bool filling = false;
            long chosenLine = 0;

            // ─── keeping the three lists in step ──────────────────────────────────

            void ShowTree()
            {
                tree.Text = draft == null || draft.Empty
                    ? Words.T("dlg.pickNpc")
                    : Sketch(draft, source);

                complaints.Text = draft == null || draft.Empty
                    ? ""
                    : string.Join(Environment.NewLine, NpcDialogueContent.Complaints(draft.Freeze()));
            }

            void ShowReplies()
            {
                var rows = new List<ReplyRow>();
                if (source != null && draft != null && chosenLine != 0)
                {
                    var under = draft.Under(chosenLine);
                    var byId = under.ToDictionary(c => c.Reply);

                    foreach (var reply in source.Replies)
                    {
                        byId.TryGetValue(reply.Id, out var choice);
                        rows.Add(new ReplyRow(reply.Id, reply.Text, choice != null,
                                              choice?.Next ?? 0,
                                              choice == null || choice.Next == 0 ? "" : Preview(source, choice.Next)));
                    }

                    // A reply that was borrowed from another NPC is not in this one's own list, so
                    // it would silently vanish from the screen while still being in the file.
                    foreach (var choice in under)
                    {
                        if (rows.Any(r => r.Reply == choice.Reply)) continue;
                        rows.Add(new ReplyRow(choice.Reply, _catalogue.Text(choice.Reply.ToString()), true,
                                              choice.Next, choice.Next == 0 ? "" : Preview(source, choice.Next)));
                    }

                    rows.Sort((a, b) => a.InLine == b.InLine ? 0 : (a.InLine ? -1 : 1));
                }

                replyList.ItemsSource = rows;
                addReply.IsEnabled = false;
                dropReply.IsEnabled = false;
                leadsTo.IsEnabled = false;

                // Why the buttons are grey. Without this the screen has a dead end in it: the
                // replies are all there, none of them can be added, and nothing says why.
                replyHint.Text = draft == null || chosenLine == 0
                    ? Words.T("dlg.pickLine")
                    : (draft.Has(chosenLine)
                           ? Words.T("dlg.chainHint")
                           : Words.T("dlg.lineNotInTree", Words.T("dlg.addLine")));
            }

            void ShowLines()
            {
                var rows = new List<LineRow>();
                if (source != null)
                {
                    foreach (var message in source.Messages)
                    {
                        rows.Add(new LineRow(message.Id, message.Text,
                                             draft?.Has(message.Id) == true,
                                             draft?.Opening == message.Id));
                    }

                    // Same as above: a line in the tree that the template no longer declares still
                    // has to be visible, or it cannot be taken out.
                    if (draft != null)
                    {
                        foreach (long message in draft.Lines.Keys)
                        {
                            if (rows.Any(r => r.Message == message)) continue;
                            rows.Add(new LineRow(message, "", true, draft.Opening == message));
                        }
                    }
                }

                lineList.ItemsSource = rows;
                addLine.IsEnabled = false;
                dropLine.IsEnabled = false;
                openHere.IsEnabled = false;

                ShowReplies();
                ShowTree();
            }

            void Count()
            {
                int written = _drafts.Values.Count(d => !d.Empty);
                counts.Text = Words.T("dlg.counts", npcs.Count.ToString("N0"), written.ToString("N0"))
                            + (_dirty ? "   ·   " + Words.T("common.unsaved") : "");
                save.IsEnabled = _dirty;
            }

            void Touch()
            {
                _dirty = true;
                Count();
                ShowTree();
                npcList.ItemsSource = Filter(npcs, search.Text);
            }

            void Pick(NpcSummary? npc)
            {
                source = npc == null ? null : _catalogue.Source(npc.Id);
                draft = npc == null ? null : DraftFor(npc.Id);
                chosenLine = 0;

                ShowLines();

                // Land on something. An NPC opens on the line its tree opens on, or on its first
                // one, because a screen that needs a second click before it shows anything is the
                // screen this one replaced.
                if (lineList.Items.Count > 0)
                {
                    var rows = (List<LineRow>)lineList.ItemsSource!;
                    int at = rows.FindIndex(r => r.Opens);
                    if (at < 0) at = rows.FindIndex(r => r.InTree);
                    lineList.SelectedIndex = at < 0 ? 0 : at;
                }
            }

            // ─── what the buttons do ──────────────────────────────────────────────

            npcList.SelectionChanged += (_, _) => Pick(npcList.SelectedItem as NpcSummary);

            lineList.SelectionChanged += (_, _) =>
            {
                if (lineList.SelectedItem is not LineRow row)
                {
                    chosenLine = 0;
                    ShowReplies();
                    return;
                }

                chosenLine = row.Message;
                addLine.IsEnabled = !row.InTree;
                dropLine.IsEnabled = row.InTree;
                openHere.IsEnabled = row.InTree && !row.Opens;
                ShowReplies();
            };

            addLine.Click += (_, _) =>
            {
                if (draft == null || chosenLine == 0 || draft.Has(chosenLine)) return;

                draft.Lines[chosenLine] = new List<DraftChoice>();
                if (draft.Opening == 0) draft.Opening = chosenLine;

                Touch();
                long keep = chosenLine;
                ShowLines();
                Reselect(lineList, keep);
            };

            dropLine.Click += (_, _) =>
            {
                if (draft == null || chosenLine == 0) return;

                draft.Lines.Remove(chosenLine);
                if (draft.Opening == chosenLine) draft.Opening = draft.Lines.Count > 0 ? draft.Lines.Keys.First() : 0;

                // Anything that pointed at it now ends the conversation instead of aiming at a line
                // that is gone.
                foreach (var choices in draft.Lines.Values)
                {
                    foreach (var choice in choices)
                    {
                        if (choice.Next == chosenLine) choice.Next = 0;
                    }
                }

                Touch();
                long keep = chosenLine;
                ShowLines();
                Reselect(lineList, keep);
            };

            openHere.Click += (_, _) =>
            {
                if (draft == null || chosenLine == 0) return;

                draft.Opening = chosenLine;
                Touch();
                long keep = chosenLine;
                ShowLines();
                Reselect(lineList, keep);
            };

            replyList.SelectionChanged += (_, _) =>
            {
                if (replyList.SelectedItem is not ReplyRow row || draft == null || chosenLine == 0)
                {
                    addReply.IsEnabled = dropReply.IsEnabled = leadsTo.IsEnabled = false;
                    return;
                }

                bool inTree = draft.Has(chosenLine);
                addReply.IsEnabled = inTree && !row.InLine;
                dropReply.IsEnabled = inTree && row.InLine;
                leadsTo.IsEnabled = inTree && row.InLine;

                filling = true;
                leadsTo.ItemsSource = Destinations(draft, source, chosenLine);
                leadsTo.SelectedIndex = 0;
                if (row.Next != 0)
                {
                    var options = (List<Destination>)leadsTo.ItemsSource!;
                    int at = options.FindIndex(d => d.Message == row.Next);
                    if (at >= 0) leadsTo.SelectedIndex = at;
                }

                filling = false;
            };

            addReply.Click += (_, _) =>
            {
                if (draft == null || chosenLine == 0) return;
                if (replyList.SelectedItem is not ReplyRow row) return;
                if (!draft.Lines.TryGetValue(chosenLine, out var choices)) return;

                choices.Add(new DraftChoice { Reply = row.Reply });
                Touch();
                ShowReplies();
                Reselect(replyList, row.Reply);
            };

            dropReply.Click += (_, _) =>
            {
                if (draft == null || chosenLine == 0) return;
                if (replyList.SelectedItem is not ReplyRow row) return;
                if (!draft.Lines.TryGetValue(chosenLine, out var choices)) return;

                choices.RemoveAll(c => c.Reply == row.Reply);
                Touch();
                ShowReplies();
                Reselect(replyList, row.Reply);
            };

            leadsTo.SelectionChanged += (_, _) =>
            {
                if (filling || draft == null || chosenLine == 0) return;
                if (replyList.SelectedItem is not ReplyRow row) return;
                if (leadsTo.SelectedItem is not Destination where) return;
                if (!draft.Lines.TryGetValue(chosenLine, out var choices)) return;

                var choice = choices.Find(c => c.Reply == row.Reply);
                if (choice == null) return;

                choice.Next = where.Message;

                // A reply that leads to a line puts that line in the tree. Making somebody add it
                // first and then come back to point at it is two steps for one intention, and it
                // was the step nobody found.
                if (where.Message != 0 && !draft.Has(where.Message))
                {
                    draft.Lines[where.Message] = new List<DraftChoice>();
                }

                Touch();
                long keep = chosenLine;
                ShowLines();
                Reselect(lineList, keep);
                Reselect(replyList, row.Reply);
            };

            // ─── borrowing a reply from the rest of the game ──────────────────────
            var borrow = Picker.Of(
                _catalogue.EveryReply(),
                reply => reply.Short(90),
                reply => reply.Id,
                Words.T("dlg.everyReply"),
                reply =>
                {
                    if (draft == null || chosenLine == 0) return;
                    if (!draft.Lines.TryGetValue(chosenLine, out var choices)) return;
                    if (choices.Any(c => c.Reply == reply.Id)) return;

                    choices.Add(new DraftChoice { Reply = reply.Id });
                    Touch();
                    ShowReplies();
                },
                width: 360);

            // Any line in the game, not only the ones this NPC declares. The ios the server sends
            // carries the line as a plain id into the game's own table of 55,037, with nothing
            // tying it to the speaker, so an NPC can be made to say any of them.
            var borrowLine = Picker.Of(
                _catalogue.EveryLine(),
                line => line.Short(90),
                line => line.Id,
                Words.T("dlg.everyLine"),
                line =>
                {
                    if (draft == null || draft.Has(line.Id)) return;

                    draft.Lines[line.Id] = new List<DraftChoice>();
                    if (draft.Opening == 0) draft.Opening = line.Id;

                    Touch();
                    ShowLines();
                    Reselect(lineList, line.Id);
                },
                width: 360);

            search.TextChanged += (_, _) => npcList.ItemsSource = Filter(npcs, search.Text);

            save.Click += (_, _) =>
            {
                var trees = new List<NpcDialogue>();
                var wrong = new List<string>();

                foreach (var pair in _drafts)
                {
                    if (pair.Value.Empty) continue;

                    var frozen = pair.Value.Freeze();
                    foreach (string complaint in NpcDialogueContent.Complaints(frozen))
                    {
                        wrong.Add($"npc {frozen.NpcId}: {complaint}");
                    }

                    trees.Add(frozen);
                }

                // Nothing is written while a tree is broken. A reply pointing at a line that is not
                // there leaves the player looking at a window with no way out, and it would be
                // found on somebody else's machine days later.
                if (wrong.Count > 0)
                {
                    complaints.Text = Words.T("dlg.notSaved", string.Join(" ", wrong));
                    return;
                }

                try
                {
                    NpcDialogueContent.Save(Paths.ContentFile(NpcDialogueContent.AuthoredFile), trees);
                    _world.ReloadNpcDialogues();
                    _dirty = false;
                    Count();
                    complaints.Text = "";
                    counts.Text += "   ·   " + Words.T("common.saved");
                }
                catch (Exception ex)
                {
                    complaints.Text = Words.T("common.couldNotSave", ex.Message);
                }
            };

            npcList.ItemsSource = Filter(npcs, null);
            Count();
            ShowTree();

            // ─── the shape of the screen ──────────────────────────────────────────
            var top = Bar(save, search, counts);

            var left = new DockPanel { LastChildFill = true };
            var header = NpcHeader();
            DockPanel.SetDock(header, Dock.Top);
            left.Children.Add(header);
            left.Children.Add(npcList);

            var linesPane = Pane(Words.T("dlg.saysHeader"), lineList,
                                 Row(addLine, dropLine, openHere, borrowLine));
            var replyControls = new StackPanel { Spacing = 4 };
            replyControls.Children.Add(replyHint);
            replyControls.Children.Add(Row(addReply, dropReply, leadsTo, borrow));

            var repliesPane = Pane(Words.T("dlg.repliesHeader"), replyList, replyControls);

            var middle = new Grid { RowDefinitions = new RowDefinitions("*,10,*") };
            Grid.SetRow(linesPane, 0);
            Grid.SetRow(repliesPane, 2);
            middle.Children.Add(linesPane);
            middle.Children.Add(repliesPane);

            var rightPanel = new DockPanel { LastChildFill = true };
            var treeTitle = Skin.Heading(Words.T("dlg.inTree"));
            DockPanel.SetDock(treeTitle, Dock.Top);
            treeTitle.Margin = new Thickness(0, 0, 0, 8);
            complaints.Margin = new Thickness(0, 10, 0, 0);
            DockPanel.SetDock(complaints, Dock.Bottom);
            rightPanel.Children.Add(treeTitle);
            rightPanel.Children.Add(complaints);
            rightPanel.Children.Add(new ScrollViewer { Content = tree });

            var split = new Grid { ColumnDefinitions = new ColumnDefinitions("300,10,*,10,340") };
            Grid.SetColumn(left, 0);
            Grid.SetColumn(middle, 2);
            var card = Skin.Card(rightPanel);
            Grid.SetColumn(card, 4);
            split.Children.Add(left);
            split.Children.Add(middle);
            split.Children.Add(card);

            var layout = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(top, Dock.Top);
            layout.Children.Add(top);
            layout.Children.Add(split);
            return layout;
        }

        // ─── The pieces ───────────────────────────────────────────────────────────

        private sealed record Destination(long Message, string Text)
        {
            public override string ToString() => Text;
        }

        /// <summary>
        /// Where a reply can lead: the end, or any line this NPC has.
        /// </summary>
        /// <remarks>
        /// Any line it <em>has</em>, not just the ones already in the tree, and that is the whole
        /// fix for chaining. Before, the list held only what had already been added, so the first
        /// reply anybody made could go nowhere but the end — the screen offered exactly one option
        /// and looked like it did not do chaining at all. Now picking a line that is not in the
        /// tree yet puts it in, which is what somebody choosing "and then he says this" means.
        /// </remarks>
        private static List<Destination> Destinations(Draft draft, NpcDialogueSource? source, long from)
        {
            var options = new List<Destination> { new Destination(0, Words.T("dlg.ends")) };
            var seen = new HashSet<long>();

            foreach (long message in draft.Lines.Keys)
            {
                if (message == from || !seen.Add(message)) continue;
                options.Add(new Destination(message, Preview(source, message)));
            }

            if (source != null)
            {
                foreach (var line in source.Messages)
                {
                    if (line.Id == from || !seen.Add(line.Id)) continue;
                    options.Add(new Destination(line.Id, Short(line.Text, 46)));
                }
            }

            return options;
        }

        /// <summary>The tree as text, so its shape can be seen without clicking through it.</summary>
        private static string Sketch(Draft draft, NpcDialogueSource? source)
        {
            var text = new StringBuilder();
            var order = new List<long>(draft.Lines.Keys);
            order.Sort((a, b) =>
            {
                if (a == draft.Opening) return -1;
                if (b == draft.Opening) return 1;
                return a.CompareTo(b);
            });

            foreach (long message in order)
            {
                text.Append(message == draft.Opening ? "▶ " : "· ");
                text.AppendLine(Preview(source, message, 70));

                foreach (var choice in draft.Under(message))
                {
                    text.Append("      → ");
                    text.Append(Short(TextOf(source, choice.Reply, isReply: true), 52));
                    text.AppendLine(choice.Next == 0
                        ? "   ✕"
                        : "   ⇢ " + Short(TextOf(source, choice.Next, isReply: false), 34));
                }

                text.AppendLine();
            }

            return text.ToString();
        }

        private static string TextOf(NpcDialogueSource? source, long id, bool isReply)
        {
            if (source == null) return id.ToString();

            var list = isReply ? source.Replies : source.Messages;
            foreach (var entry in list)
            {
                if (entry.Id == id) return entry.Text.Length > 0 ? entry.Text : id.ToString();
            }

            return id.ToString();
        }

        private static string Preview(NpcDialogueSource? source, long message, int limit = 46)
            => Short(TextOf(source, message, isReply: false), limit);

        private static string Short(string text, int limit)
        {
            string flat = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (flat.Length == 0) return "…";
            return flat.Length <= limit ? flat : flat[..limit] + "…";
        }

        private static void Reselect(ListBox list, long id)
        {
            if (list.ItemsSource is IEnumerable<LineRow> lines)
            {
                var rows = lines.ToList();
                list.SelectedIndex = rows.FindIndex(r => r.Message == id);
                return;
            }

            if (list.ItemsSource is IEnumerable<ReplyRow> replies)
            {
                var rows = replies.ToList();
                list.SelectedIndex = rows.FindIndex(r => r.Reply == id);
            }
        }

        private void Load()
        {
            _catalogue = new NpcCatalogue(_world.Text, _world.Complaints.Add);
            foreach (var pair in _world.NpcDialogues.Rows) Adopt(pair.Value.Value);
            _loaded = true;
        }

        private void Adopt(NpcDialogue dialogue)
        {
            var draft = new Draft { NpcId = dialogue.NpcId, Opening = dialogue.Opening };
            foreach (var line in dialogue.Lines)
            {
                var choices = new List<DraftChoice>();
                foreach (var choice in line.Choices)
                {
                    choices.Add(new DraftChoice
                    {
                        Reply = choice.Reply,
                        Next = choice.Next,
                        Quest = choice.Quest,
                        Step = choice.Step,
                        StartsQuest = choice.StartsQuest,
                        AfterQuest = choice.AfterQuest,
                    });
                }

                draft.Lines[line.Message] = choices;
            }

            _drafts[dialogue.Key] = draft;
        }

        private Draft DraftFor(int npcId)
        {
            // Every tree written here is for every map the NPC stands on. Per-map openings are in
            // the file format and in the server, because the real game does make that distinction,
            // but there is no reason to put the choice in front of somebody until a second opening
            // is actually wanted.
            var key = new NpcDialogueKey(npcId, NpcDialogueKey.AnyMap);
            if (_drafts.TryGetValue(key, out var draft)) return draft;

            draft = new Draft { NpcId = npcId };
            _drafts[key] = draft;
            return draft;
        }

        private bool HasTree(int npcId)
            => _drafts.TryGetValue(new NpcDialogueKey(npcId, NpcDialogueKey.AnyMap), out var draft)
            && !draft.Empty;

        private static List<NpcSummary> Filter(List<NpcSummary> all, string? needle)
        {
            needle = (needle ?? "").Trim();
            if (needle.Length == 0) return all;

            return all.Where(npc => npc.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
                                 || npc.Id.ToString().Contains(needle, StringComparison.Ordinal))
                      .ToList();
        }

        private Control Missing()
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(Skin.Heading(Words.T("missing.world")));
            panel.Children.Add(OverviewPage.Mono(Words.T("missing.lookedIn", Paths.WorldDb)));
            return Skin.Card(panel);
        }

        // ─── Rows ─────────────────────────────────────────────────────────────────

        private static Control Bar(params Control[] controls)
        {
            var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            foreach (var control in controls)
            {
                control.Margin = new Thickness(0, 0, 12, 6);
                if (control is TextBox box) box.Width = 200;
                else control.VerticalAlignment = VerticalAlignment.Center;
                bar.Children.Add(control);
            }

            return bar;
        }

        private static Control Row(params Control[] controls)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var control in controls)
            {
                control.Margin = new Thickness(0, 0, 8, 4);
                row.Children.Add(control);
            }

            return row;
        }

        private static Control Pane(string heading, Control list, Control controls)
        {
            var panel = new DockPanel { LastChildFill = true };

            var title = Skin.Heading(heading);
            title.Margin = new Thickness(0, 0, 0, 8);
            DockPanel.SetDock(title, Dock.Top);
            DockPanel.SetDock(controls, Dock.Bottom);

            panel.Children.Add(title);
            panel.Children.Add(controls);
            panel.Children.Add(list);
            return panel;
        }

        /// <summary>
        /// The column header the NPC list did not have.
        /// </summary>
        /// <remarks>
        /// "1/18" next to a name meant nothing without it, which is a fair complaint about a number
        /// that is on every row of the screen.
        /// </remarks>
        private static Control NpcHeader()
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("52,*,84"),
                Margin = new Thickness(10, 0, 10, 6),
            };

            var id = Skin.Fixed(Words.T("common.id"), Skin.TextFaintBrush);
            Grid.SetColumn(id, 0);
            row.Children.Add(id);

            var name = Skin.Fixed(Words.T("common.name"), Skin.TextFaintBrush);
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            var howMany = Skin.Fixed(Words.T("dlg.columns"), Skin.TextFaintBrush);
            howMany.FontSize = 11;
            Grid.SetColumn(howMany, 2);
            row.Children.Add(howMany);

            return row;
        }

        private static Control NpcLine(NpcSummary npc, bool hasTree)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("52,*,84") };

            var id = Skin.Fixed(npc.Id.ToString(), Skin.TextFaintBrush);
            Grid.SetColumn(id, 0);
            row.Children.Add(id);

            var name = new TextBlock
            {
                Text = npc.Name.Length > 0 ? npc.Name : "(?)",
                Foreground = hasTree ? Skin.DoneBrush : Skin.TextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 12.5,
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            var counts = Skin.Fixed($"{npc.Messages} / {npc.Replies}", Skin.TextFaintBrush);
            Grid.SetColumn(counts, 2);
            row.Children.Add(counts);

            return row;
        }

        /// <summary>
        /// One line the NPC can say. Wrapped, not trimmed.
        /// </summary>
        /// <remarks>
        /// It was one clipped line before, in a box with room for six. A dialogue is decided by
        /// reading it, so a screen that shows the first forty characters of every sentence is a
        /// screen you cannot decide anything on.
        /// </remarks>
        private static Control Line(LineRow row)
        {
            var panel = new Grid { ColumnDefinitions = new ColumnDefinitions("62,74,*") };

            var id = Skin.Fixed(row.Message.ToString(), Skin.TextFaintBrush);
            id.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(id, 0);
            panel.Children.Add(id);

            var badge = Skin.Fixed(
                row.Opens ? Words.T("dlg.opens") : (row.InTree ? Words.T("dlg.inTree") : Words.T("dlg.notInTree")),
                row.Opens ? Skin.AuthoredBrush : (row.InTree ? Skin.DoneBrush : Skin.TextFaintBrush));
            badge.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(badge, 1);
            panel.Children.Add(badge);

            var text = new TextBlock
            {
                Text = row.Text.Length > 0 ? row.Text : row.Message.ToString(),
                Foreground = row.InTree ? Skin.TextBrush : Skin.TextSoftBrush,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
            };
            Grid.SetColumn(text, 2);
            panel.Children.Add(text);

            return panel;
        }

        private static Control Reply(ReplyRow row)
        {
            var panel = new Grid { ColumnDefinitions = new ColumnDefinitions("62,74,*,Auto") };

            var id = Skin.Fixed(row.Reply.ToString(), Skin.TextFaintBrush);
            id.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(id, 0);
            panel.Children.Add(id);

            var badge = Skin.Fixed(row.InLine ? Words.T("dlg.inTree") : Words.T("dlg.notInTree"),
                                    row.InLine ? Skin.DoneBrush : Skin.TextFaintBrush);
            badge.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(badge, 1);
            panel.Children.Add(badge);

            var text = new TextBlock
            {
                Text = row.Text.Length > 0 ? row.Text : row.Reply.ToString(),
                Foreground = row.InLine ? Skin.TextBrush : Skin.TextSoftBrush,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
            };
            Grid.SetColumn(text, 2);
            panel.Children.Add(text);

            var next = Skin.Fixed(row.InLine
                ? (row.Next == 0 ? "✕" : "⇢ " + row.NextText)
                : "", row.Next == 0 ? Skin.TextFaintBrush : Skin.AuthoredBrush);
            next.VerticalAlignment = VerticalAlignment.Top;
            next.MaxWidth = 190;
            Grid.SetColumn(next, 3);
            panel.Children.Add(next);

            return panel;
        }
    }
}
