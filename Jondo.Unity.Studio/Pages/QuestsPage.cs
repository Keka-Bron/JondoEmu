using Jondo.Unity.Sprites;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Jondo.Unity.Launcher;
using Jondo.Unity.Studio.Controls;
using Jondo.Unity.Studio.Data;
using Jondo.Unity.Studio.Ui;
using Jondo.Unity.World.Quests;

namespace Jondo.Unity.Studio.Pages
{
    /// <summary>
    /// The quests, and the line of dialogue each step is handed over on.
    /// </summary>
    /// <remarks>
    /// <b>Read only.</b> Not for want of an engine any more — the server plays quests now, off this
    /// same catalogue — but because nothing here writes one yet. What it does is make the data
    /// legible, which it was not: the client keeps quests in six Unity dumps totalling 11.4 MB of
    /// serialisation scaffolding, in a folder the repository does not even carry.
    ///
    /// The column that makes it worth building before the engine is the dialogue. A quest step
    /// declares the NPC line that hands it over, and the emulator already has both halves — the
    /// 55,037 lines of NPC dialogue and the authored dialogue trees. 1,260 of the 2,225 steps carry
    /// one and every single one of them resolves to real text, which means the engine, when it is
    /// written, has a join to hang itself on rather than a table of numbers.
    ///
    /// It was checked against a capture rather than assumed. In
    /// <c>Misiones\hablar con NPC y aceptar una mision</c> the client opens a dialogue on map
    /// 212863492, the server walks it to line 50071, the player picks the last reply, and only then
    /// does the server push <c>ief {2432}</c> — after the reply, not on arriving at the line, which
    /// is what the engine copies. Quest 2432 says it is handed out by NPC 6617 on map 212863492,
    /// and its only step declares dialogId 50071.
    ///
    /// The prerequisite chain is read out of the start criterion, which is one string per quest
    /// holding up to 29 different operators. Exactly one of them is read here — <c>Qf</c>, "quest
    /// finished" — because that is the one that chains a questline, and 990 quests have one.
    /// </remarks>
    public sealed class QuestsPage : IStudioPage
    {
        private readonly WorldData _world;

        private QuestCatalogue? _quests;
        private NpcCatalogue? _npcs;
        private MapCatalogue? _maps;
        private NpcDetails? _details;

        private readonly NpcSprites _sprites = new NpcSprites();
        private Dictionary<int, string> _npcNames = new Dictionary<int, string>();
        private Dictionary<int, string> _npcLooks = new Dictionary<int, string>();
        private bool _loaded;

        public QuestsPage(WorldData world) => _world = world;

        public string TitleKey => "nav.quests";

        public override string ToString() => Words.T(TitleKey);

        /// <summary>One line of the step list: a step, or an objective under it.</summary>
        private sealed record Line(bool IsStep, int Number, QuestStep Step, QuestObjective? Objective);

        public Control Build()
        {
            if (!_loaded) Load();

            if (_quests == null || !_quests.Ready)
            {
                var missing = new StackPanel { Spacing = 8 };
                missing.Children.Add(Skin.Heading(Words.T("quest.none")));
                missing.Children.Add(OverviewPage.Mono(Words.T("missing.lookedIn", Paths.QuestsJson)));
                missing.Children.Add(new TextBlock
                {
                    Foreground = Skin.TextSoftBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Text = Words.T("quest.howToBuild"),
                });
                return Skin.Card(missing);
            }

            var all = _quests.All();

            var status = new TextBlock
            {
                Foreground = Skin.TextSoftBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var steps = new ListBox
            {
                ItemTemplate = new FuncDataTemplate<Line>((line, _) => Row(line), supportsRecycling: true),
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(steps, ScrollBarVisibility.Disabled);

            var grid = new CellGrid();
            var portrait = new Image { Width = 84, Height = 108, VerticalAlignment = VerticalAlignment.Top };
            var card = new SelectableTextBlock
            {
                FontFamily = Skin.Mono,
                FontSize = 12.5,
                Foreground = Skin.TextBrush,
                TextWrapping = TextWrapping.Wrap,
                Text = Words.T("quest.pick"),
            };

            var chain = new StackPanel { Spacing = 4 };
            var said = new SelectableTextBlock
            {
                Foreground = Skin.TextSoftBrush,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
                Text = "",
            };

            Quest? chosen = null;

            // ─── what the right-hand side says ────────────────────────────────────

            void ShowQuest(Quest? quest)
            {
                chosen = quest;
                chain.Children.Clear();

                if (quest == null)
                {
                    card.Text = Words.T("quest.pick");
                    portrait.Source = null;
                    steps.ItemsSource = Array.Empty<Line>();
                    said.Text = "";
                    grid.Show(null);
                    return;
                }

                var giver = quest.Givers.Count > 0 ? quest.Givers[0] : default;
                portrait.Source = giver.NpcId != 0 ? _sprites.Of(LookOf(giver.NpcId)) : null;

                card.Text = Card(quest);
                steps.ItemsSource = Lines(quest);
                said.Text = "";

                // The map the quest starts on, so the giver can be seen where they stand.
                grid.Show(giver.MapId != 0 && _world.Maps.TryGetValue(giver.MapId, out var cells)
                    ? cells : null);

                var marks = new Dictionary<int, CellMark>();
                foreach (var pair in _world.NpcPlacements.Rows)
                {
                    if (pair.Key.MapId != giver.MapId) continue;

                    bool isGiver = pair.Key.NpcId == giver.NpcId;
                    marks[pair.Key.Cell] = new CellMark
                    {
                        Colour = isGiver ? Skin.AuthoredBrush : Skin.MeasuredBrush,
                        Label = Short(NameOf(pair.Key.NpcId)),
                        Icon = _sprites.Of(LookOf(pair.Key.NpcId)),
                    };
                }

                grid.Mark(marks);

                // The questline this one sits in, both ways, one click deep. Deeper would be a
                // graph and a graph on a side panel is unreadable.
                foreach (int before in quest.Requires)
                {
                    chain.Children.Add(Step(Words.T("quest.after"), _quests.Of(before), q => ShowQuest(q)));
                }

                foreach (var next in Unlocks(quest.Id))
                {
                    chain.Children.Add(Step(Words.T("quest.leadsTo"), next, q => ShowQuest(q)));
                }

                if (chain.Children.Count == 0)
                {
                    chain.Children.Add(new TextBlock
                    {
                        Foreground = Skin.TextFaintBrush,
                        FontSize = 12,
                        Text = Words.T("quest.standsAlone"),
                    });
                }
            }

            steps.SelectionChanged += (_, _) =>
            {
                said.Text = steps.SelectedItem is Line line && line.IsStep && line.Step.DialogId > 0
                    ? Spoken(line.Step)
                    : "";
            };

            var picker = Picker.Of(all, q => q.Name.Length > 0 ? q.Name : q.Id.ToString(),
                                   q => q.Id, Words.T("quest.search"),
                                   q => ShowQuest(q), 320);

            status.Text = Words.T("quest.counts",
                _quests.QuestCount.ToString("N0"),
                _quests.StepCount.ToString("N0"),
                _quests.SpokenSteps.ToString("N0"),
                _quests.GatedQuests.ToString("N0"));

            // ─── the layout ───────────────────────────────────────────────────────

            var bar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children = { picker, status },
            };

            var left = new DockPanel { MinWidth = 430 };
            var stepsHeading = Skin.Label(Words.T("quest.steps"));
            DockPanel.SetDock(stepsHeading, Dock.Top);
            left.Children.Add(stepsHeading);

            var saidCard = Skin.Card(said, 10);
            DockPanel.SetDock(saidCard, Dock.Bottom);
            left.Children.Add(saidCard);
            left.Children.Add(steps);

            var detail = new StackPanel { Spacing = 10, Width = 330 };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            head.Children.Add(portrait);
            head.Children.Add(card);
            detail.Children.Add(Skin.Card(head));
            detail.Children.Add(Skin.Card(new StackPanel
            {
                Spacing = 6,
                Children = { Skin.Label(Words.T("quest.chain")), chain },
            }));

            var middle = new DockPanel();
            DockPanel.SetDock(detail, Dock.Right);
            middle.Children.Add(detail);
            middle.Children.Add(Skin.Card(grid));

            var body = new DockPanel();
            DockPanel.SetDock(left, Dock.Left);
            body.Children.Add(left);
            body.Children.Add(middle);

            var page = new DockPanel { Margin = new Avalonia.Thickness(14) };
            DockPanel.SetDock(bar, Dock.Top);
            page.Children.Add(bar);
            page.Children.Add(body);

            if (all.Count > 0) ShowQuest(null);
            return page;
        }

        // ─── the pieces ───────────────────────────────────────────────────────────

        /// <summary>The quest's own card: who gives it, where, and what it takes to be offered.</summary>
        private string Card(Quest quest)
        {
            var text = new StringBuilder();
            text.Append(quest.Name.Length > 0 ? quest.Name : "#" + quest.Id).Append('\n');
            text.Append('#').Append(quest.Id);
            if (quest.Category.Length > 0) text.Append("  ·  ").Append(quest.Category);
            text.Append('\n');

            if (quest.LevelMin > 0)
            {
                text.Append('\n').Append(Words.T("quest.level",
                    quest.LevelMin == quest.LevelMax
                        ? quest.LevelMin.ToString()
                        : $"{quest.LevelMin}–{quest.LevelMax}"));
            }

            var flags = new List<string>();
            if (quest.Dungeon) flags.Add(Words.T("quest.dungeon"));
            if (quest.Party) flags.Add(Words.T("quest.party"));
            if (quest.Event) flags.Add(Words.T("quest.event"));
            if (quest.Repeatable) flags.Add(Words.T("quest.repeatable"));
            if (flags.Count > 0) text.Append('\n').Append(string.Join("  ·  ", flags));

            text.Append("\n\n");
            if (quest.Givers.Count == 0)
            {
                text.Append(Words.T("quest.noGiver"));
            }
            else
            {
                foreach (var giver in quest.Givers)
                {
                    string name = NameOf(giver.NpcId);
                    text.Append(Words.T("quest.givenBy",
                        name.Length > 0 ? name : "#" + giver.NpcId, Where(giver.MapId)));
                    text.Append('\n');
                }
            }

            if (quest.Criterion.Length > 0)
            {
                text.Append('\n').Append(Words.T("quest.criterion")).Append('\n')
                    .Append(quest.Criterion);
            }

            return text.ToString();
        }

        /// <summary>The step list, with each step's objectives folded in underneath it.</summary>
        private List<Line> Lines(Quest quest)
        {
            var lines = new List<Line>();
            int number = 0;
            foreach (var step in quest.Steps)
            {
                number++;
                lines.Add(new Line(true, number, step, null));
                foreach (var objective in step.Objectives)
                {
                    lines.Add(new Line(false, number, step, objective));
                }
            }

            return lines;
        }

        private Control Row(Line line)
        {
            if (line.IsStep)
            {
                var head = new StackPanel { Spacing = 2, Margin = new Avalonia.Thickness(0, 6, 0, 2) };
                var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                title.Children.Add(new TextBlock
                {
                    Foreground = Skin.TextFaintBrush,
                    FontFamily = Skin.Mono,
                    Text = line.Number.ToString("00"),
                });
                title.Children.Add(new TextBlock
                {
                    Foreground = Skin.TextBrush,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Text = line.Step.Name.Length > 0 ? line.Step.Name : "#" + line.Step.Id,
                });

                // The badge that says this step is handed over by somebody saying something. It is
                // the only thing on the screen the quest engine will actually need to hook onto.
                if (line.Step.DialogId > 0)
                {
                    title.Children.Add(new TextBlock
                    {
                        Foreground = Skin.DerivedBrush,
                        FontSize = 11.5,
                        VerticalAlignment = VerticalAlignment.Center,
                        Text = Words.T("quest.spoken"),
                    });
                }

                head.Children.Add(title);

                if (line.Step.Description.Length > 0)
                {
                    head.Children.Add(new TextBlock
                    {
                        Foreground = Skin.TextSoftBrush,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Avalonia.Thickness(24, 0, 0, 0),
                        Text = line.Step.Description,
                    });
                }

                var prize = Prize(line.Step);
                if (prize.Length > 0)
                {
                    head.Children.Add(new TextBlock
                    {
                        Foreground = Skin.DoneBrush,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Avalonia.Thickness(24, 0, 0, 0),
                        Text = prize,
                    });
                }

                return head;
            }

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Avalonia.Thickness(24, 0, 0, 0),
            };
            row.Children.Add(new TextBlock
            {
                Foreground = Skin.TextFaintBrush,
                FontFamily = Skin.Mono,
                FontSize = 12,
                Text = "·",
            });
            row.Children.Add(new TextBlock
            {
                Foreground = Skin.TextSoftBrush,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Text = Describe(line.Objective!),
            });

            return row;
        }

        /// <summary>
        /// One objective, in the client's own words with the numbers filled in.
        /// </summary>
        /// <remarks>
        /// Only the NPC slot is resolved to a name. The other slots hold monster ids, item ids and
        /// map ids depending on the type, and guessing which is which would put a monster's name
        /// where an item belongs — worse than the number, because a number is obviously a number.
        /// </remarks>
        private string Describe(QuestObjective objective)
        {
            string said = _quests!.Describe(objective, (slot, value) =>
                slot == 1 && objective.NpcId != 0 ? NameOf(value) : "");

            if (objective.MapId != 0)
            {
                said += "   " + Where(objective.MapId);
            }
            else if (objective.Coords is { } corner)
            {
                said += $"   ({corner.X},{corner.Y})";
            }

            return said;
        }

        /// <summary>What the NPC says to hand this step over.</summary>
        private string Spoken(QuestStep step)
        {
            string line = _npcs?.LineText(step.DialogId) ?? "";
            return line.Length > 0
                ? Words.T("quest.saysThis", step.DialogId.ToString()) + "\n" + line
                : Words.T("quest.lineMissing", step.DialogId.ToString());
        }

        private string Prize(QuestStep step)
        {
            var parts = new List<string>();
            foreach (var reward in step.Rewards)
            {
                if (reward.Empty) continue;
                foreach (var (item, count) in reward.Items)
                {
                    string name = _details?.ItemName(item) ?? "";
                    parts.Add(count > 1
                        ? $"{(name.Length > 0 ? name : "#" + item)} ×{count}"
                        : name.Length > 0 ? name : "#" + item);
                }

                foreach (int spell in reward.Spells) parts.Add(Words.T("quest.spellPrize", spell.ToString()));
                foreach (int emote in reward.Emotes) parts.Add(Words.T("quest.emotePrize", emote.ToString()));
                foreach (int title in reward.Titles) parts.Add(Words.T("quest.titlePrize", title.ToString()));
            }

            return parts.Count == 0 ? "" : Words.T("quest.gives", string.Join(", ", parts));
        }

        /// <summary>One link of the questline, clickable.</summary>
        private Control Step(string label, Quest? quest, Action<Quest> go)
        {
            if (quest == null) return new TextBlock { Text = label, Foreground = Skin.TextFaintBrush };

            var button = new Button
            {
                Content = $"{label}  {(quest.Name.Length > 0 ? quest.Name : "#" + quest.Id)}",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                FontSize = 12.5,
            };
            button.Click += (_, _) => go(quest);
            return button;
        }

        /// <summary>
        /// The quests this one opens up.
        /// </summary>
        /// <remarks>
        /// Worked out by walking every quest's prerequisites rather than kept as a reverse index,
        /// because it is asked for once per click over 1,976 rows and an index would be one more
        /// thing to keep in step with the catalogue. Capped, because a few quests unlock dozens and
        /// a side panel with forty buttons on it is not a side panel.
        /// </remarks>
        private List<Quest> Unlocks(int questId)
        {
            var found = new List<Quest>();
            foreach (var quest in _quests!.All())
            {
                if (quest.Requires.Contains(questId)) found.Add(quest);
                if (found.Count == 8) break;
            }

            return found;
        }

        private string Where(long mapId)
        {
            var place = _maps?.Of(mapId);
            return place != null ? place.ToString() : "#" + mapId;
        }

        private string NameOf(int npcId) => _npcNames.TryGetValue(npcId, out string? name) ? name : "";

        private string LookOf(int npcId) => _npcLooks.TryGetValue(npcId, out string? look) ? look : "";

        private static string Short(string name)
            => name.Length <= 9 ? name : name.Substring(0, 8) + "…";

        private void Load()
        {
            _quests = new QuestCatalogue(_world.Text, _world.Complaints.Add);
            _npcs = new NpcCatalogue(_world.Text, _world.Complaints.Add);
            _maps = new MapCatalogue(_world.Text, _world.Complaints.Add);
            _details = new NpcDetails(_world.Text, _world.Complaints.Add);

            _npcNames = new Dictionary<int, string>();
            _npcLooks = new Dictionary<int, string>();
            if (_npcs.Ready)
            {
                foreach (var npc in _npcs.All())
                {
                    _npcNames[npc.Id] = npc.Name;
                    _npcLooks[npc.Id] = npc.Look;
                }
            }

            _loaded = true;
        }
    }
}
