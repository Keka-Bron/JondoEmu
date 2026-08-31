using Jondo.Unity.Sprites;
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
using Jondo.Unity.Studio.Controls;
using Jondo.Unity.Studio.Data;
using Jondo.Unity.Studio.Ui;
using Jondo.Unity.World.Content;

namespace Jondo.Unity.Studio.Pages
{
    /// <summary>
    /// Where every NPC stands — and where it is put.
    /// </summary>
    /// <remarks>
    /// The provenance column is not decoration. Six months from now nobody will remember whether a
    /// cell number was measured off a capture or typed in by hand, and without it on screen the two
    /// become indistinguishable.
    ///
    /// What is saved is the <em>difference</em> from what the captures measured, never a copy of
    /// everything shown. See <see cref="NpcSpawnContent.Delta"/> for why that rule is load-bearing:
    /// a file with all 422 rows in it would shadow the measured file for ever, and nobody would
    /// notice the day a regeneration stopped reaching the world.
    /// </remarks>
    public sealed class NpcPlacementsPage : IStudioPage
    {
        private readonly WorldData _world;

        /// <summary>
        /// What the generated files say, on their own. The floor the delta is measured against.
        /// </summary>
        /// <remarks>
        /// Both regenerable layers, not just the captured one, and that is the whole reason this
        /// field exists. The authored file is a set of deltas from whatever a tool already writes;
        /// if the derived placements were left out of this floor, then every one of the 2,009 of
        /// them would count as a change the moment anything else on the page was saved, and the
        /// authored file would swallow the lot. From then on re-running the derivation would never
        /// reach the world again — the exact silent shadowing the layers exist to prevent.
        /// </remarks>
        private readonly Dictionary<NpcSpawnKey, NpcSpawn> _generated = new Dictionary<NpcSpawnKey, NpcSpawn>();

        /// <summary>
        /// Which of those came from the derived file: right map, guessed cell.
        /// </summary>
        /// <remarks>
        /// Kept apart only so the map can paint them differently. A captured placement is where an
        /// NPC was actually seen standing; a derived one is where the quest catalogue says the NPC
        /// belongs, on a cell nobody has ever measured. Same layer behaviour, very different
        /// confidence, and the person dragging them around should be able to tell at a glance.
        /// </remarks>
        private readonly HashSet<NpcSpawnKey> _guessed = new HashSet<NpcSpawnKey>();

        /// <summary>What the world should look like when this is saved.</summary>
        private readonly Dictionary<NpcSpawnKey, NpcSpawn> _wanted = new Dictionary<NpcSpawnKey, NpcSpawn>();

        private readonly MonsterIcons _icons = new MonsterIcons();

        /// <summary>The NPCs themselves, drawn out of the client's bones.</summary>
        private readonly NpcSprites _sprites = new NpcSprites();

        /// <summary>Monster id to the drawing it uses. The picture is filed under the drawing.</summary>
        private Dictionary<int, int> _gfx = new Dictionary<int, int>();

        private NpcCatalogue? _npcNames;
        private NpcDetails? _details;
        private MonsterCatalogue? _monsters;
        private MapCatalogue? _maps;
        private List<NpcSummary> _all = new List<NpcSummary>();
        private Dictionary<int, string> _byId = new Dictionary<int, string>();
        private Dictionary<int, string> _looks = new Dictionary<int, string>();

        private bool _loaded;
        private bool _dirty;

        public NpcPlacementsPage(WorldData world) => _world = world;

        public string TitleKey => "nav.placements";

        public override string ToString() => Words.T(TitleKey);

        private sealed record Row(NpcSpawnKey Key, long Map, int Npc, string Name, int Cell,
                                  int Facing, string From, bool Authored);

        public Control Build()
        {
            if (!_loaded) Load();

            var list = new ListBox
            {
                ItemTemplate = new FuncDataTemplate<Row>((row, _) => Line(row), supportsRecycling: true),
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);

            var grid = new CellGrid();
            var search = new TextBox { Watermark = Words.T("common.search"), Width = 210, FontSize = 12.5 };
            var status = new TextBlock { Foreground = Skin.TextSoftBrush, VerticalAlignment = VerticalAlignment.Center };
            var save = new Button { Content = Words.T("common.save"), IsEnabled = _dirty };

            var mapField = new MapField(_maps!, 330);
            var facing = new ComboBox
            {
                Width = 118,
                ItemsSource = new[] { "0 ↘", "1 ↓", "2 ↙", "3 ←", "4 ↖", "5 ↑", "6 ↗", "7 →" },
                SelectedIndex = 1,
            };

            var drop = new Button { Content = Words.T("common.remove"), IsEnabled = false };
            var reset = new Button { Content = Words.T("npc.reset"), IsEnabled = false };
            var resetAll = new Button { Content = Words.T("npc.resetAll"), IsEnabled = false };
            var deselect = new Button { Content = Words.T("common.deselect"), IsEnabled = false };
            var hint = new TextBlock { Foreground = Skin.TextSoftBrush, TextWrapping = TextWrapping.Wrap };

            var portrait = new Image { Width = 84, Height = 108, VerticalAlignment = VerticalAlignment.Top };
            var card = new SelectableTextBlock
            {
                FontFamily = Skin.Mono,
                FontSize = 12.5,
                Foreground = Skin.TextBrush,
                TextWrapping = TextWrapping.Wrap,
                Text = Words.T("npc.pickToSee"),
            };

            var view = new ToggleButton
            {
                Content = Words.T("common.editView"),
                MinWidth = 132,
                IsChecked = false,
            };

            long drawn = 0;
            Row? chosen = null;
            NpcSummary? toPlace = null;

            var picker = Picker.Of(_all, npc => npc.Name.Length > 0 ? npc.Name : npc.Id.ToString(),
                                   npc => npc.Id, Words.T("npc.pickToPlace"),
                                   npc => { toPlace = npc; Hint(); }, 280,
                                   npc => _sprites.Of(npc.Look));

            void Hint()
            {
                if (drawn == 0) hint.Text = Words.T("npc.hintNoMap");
                else if (chosen != null) hint.Text = Words.T("npc.hintMove", chosen.Name.Length > 0 ? chosen.Name : chosen.Npc.ToString());
                else if (toPlace != null) hint.Text = Words.T("npc.hintPlace", toPlace.Name.Length > 0 ? toPlace.Name : toPlace.Id.ToString());
                else hint.Text = Words.T("npc.hintPick");
            }

            void Describe(Row? row)
            {
                if (row == null)
                {
                    card.Text = Words.T("npc.pickToSee");
                    portrait.Source = null;
                    return;
                }

                portrait.Source = _sprites.Of(LookOf(row.Npc));

                var detail = _details?.Of(row.Npc);
                var text = new StringBuilder();
                text.AppendLine($"{row.Name}   ·   {row.Npc}");
                text.AppendLine();
                text.AppendLine($"{Words.T("common.map")}    {row.Map}");
                text.AppendLine($"{Words.T("common.cell")}    {row.Cell}   {Words.T("common.facing")} {row.Facing}");
                text.AppendLine($"{Words.T("common.name")}    {row.From}");

                if (detail != null)
                {
                    text.AppendLine();

                    var does = new List<string>();
                    foreach (int action in detail.Actions)
                    {
                        string said = NpcDetails.Say(action);
                        does.Add(said.Length > 0 ? $"{action} {said}" : action.ToString());
                    }

                    text.AppendLine($"{Words.T("npc.does")}    " +
                                    (does.Count > 0 ? string.Join(", ", does) : Words.T("npc.nothing")));

                    text.AppendLine($"{Words.T("npc.says")}    " +
                                    (detail.Messages > 0 || detail.Replies > 0
                                        ? Words.T("npc.linesReplies", detail.Messages, detail.Replies)
                                        : Words.T("npc.nothing")));

                    if (detail.SellsCount > 0)
                    {
                        text.AppendLine();
                        text.AppendLine($"{Words.T("npc.sells")}   {detail.SellsCount}");
                        foreach (string item in detail.Sells) text.AppendLine("   · " + item);

                        int rest = detail.SellsCount - detail.Sells.Count;
                        if (rest > 0) text.AppendLine("   " + Words.T("npc.andMore", rest));
                    }

                    if (detail.Look.Length > 0)
                    {
                        text.AppendLine();
                        text.AppendLine(detail.Look);
                    }
                }

                card.Text = text.ToString();
            }

            void Redraw()
            {
                grid.Show(drawn != 0 && _world.Maps.TryGetValue(drawn, out var cells) ? cells : null);

                var marks = new Dictionary<int, CellMark>();

                // The monster groups too, and not as a courtesy: putting an NPC on a cell a group
                // already stands on is a mistake that only shows up in the game.
                if (drawn != 0 && _monsters is { Ready: true })
                {
                    foreach (var group in _monsters.GroupsOn(drawn))
                    {
                        marks[group.Cell] = new CellMark
                        {
                            Colour = new SolidColorBrush(Color.FromArgb(0xAA, 0xE0, 0x70, 0x5F)),
                            Label = "×" + group.Members.Count,
                            Icon = group.Members.Count > 0 ? _icons.Of(Gfx(group.Members[0].Monster)) : null,
                        };
                    }
                }

                foreach (var pair in _wanted)
                {
                    if (pair.Key.MapId != drawn) continue;

                    bool generated = _generated.ContainsKey(pair.Key);
                    marks[pair.Key.Cell] = new CellMark
                    {
                        // Three colours, because there are three things worth telling apart: a
                        // person put it here, a capture saw it here, or the quest catalogue says
                        // it belongs on this map and the cell underneath it is a placeholder.
                        Colour = !generated ? Skin.AuthoredBrush
                               : _guessed.Contains(pair.Key) ? Skin.DerivedBrush
                               : Skin.MeasuredBrush,
                        Label = Short(Name(pair.Key.NpcId)),
                        Icon = _sprites.Of(LookOf(pair.Key.NpcId)),
                    };
                }

                grid.Mark(marks);
                grid.Select(chosen != null && chosen.Map == drawn ? chosen.Cell : -1);

                // What would land where the pointer is. Placing something you cannot see until
                // after you have clicked is guessing, and on a grid of 560 cells it is guessing
                // three times.
                int ghost = chosen?.Npc ?? toPlace?.Id ?? 0;

                grid.Preview(ghost == 0 || drawn == 0
                    ? null
                    : new CellMark
                    {
                        Colour = Skin.AuthoredBrush,
                        Label = Short(Name(ghost)),
                        Icon = _sprites.Of(LookOf(ghost)),
                    });
            }

            void Show()
            {
                string needle = (search.Text ?? "").Trim();
                var all = Rows();
                var shown = needle.Length == 0
                    ? all
                    : all.Where(row => row.Map.ToString().Contains(needle, StringComparison.Ordinal)
                                    || row.Npc.ToString().Contains(needle, StringComparison.Ordinal)
                                    || row.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase))
                         .ToList();

                list.ItemsSource = shown;

                var text = new StringBuilder();
                text.Append(shown.Count == all.Count
                    ? Words.T("npc.placements", all.Count.ToString("N0"))
                    : Words.T("npc.someOf", shown.Count.ToString("N0"), all.Count.ToString("N0")));

                var (rows, removed) = NpcSpawnContent.Delta(_generated, _wanted);
                if (rows.Count > 0 || removed.Count > 0)
                {
                    text.Append("   ·   ").Append(Words.T("npc.delta", rows.Count, removed.Count));
                }

                if (_dirty) text.Append("   ·   ").Append(Words.T("common.unsaved"));
                status.Text = text.ToString();
                save.IsEnabled = _dirty;
                resetAll.IsEnabled = rows.Count > 0 || removed.Count > 0;
                if (chosen != null) reset.IsEnabled = Moved(chosen.Npc).HasValue;
            }

            void Touch()
            {
                _dirty = true;
                Show();
                Redraw();
            }

            void Open(long mapId)
            {
                drawn = mapId;
                mapField.Set(mapId);
                Redraw();
                Hint();
            }

            mapField.Chosen += place => Open(place.MapId);

            view.IsCheckedChanged += (_, _) =>
            {
                bool game = view.IsChecked == true;
                grid.View = game ? GridView.Game : GridView.Editing;
                view.Content = game ? Words.T("common.gameView") : Words.T("common.editView");
            };

            list.SelectionChanged += (_, _) =>
            {
                chosen = list.SelectedItem as Row;
                Describe(chosen);
                drop.IsEnabled = chosen != null;
                deselect.IsEnabled = chosen != null;

                // Only offer to put it back when there is something to put it back to, and when it
                // is not already there. A button that does nothing is worse than no button.
                reset.IsEnabled = chosen != null && Moved(chosen.Npc).HasValue;

                if (chosen != null)
                {
                    if (chosen.Map != drawn) Open(chosen.Map);
                    facing.SelectedIndex = Math.Clamp(chosen.Facing, 0, 7);
                }

                Redraw();
                Hint();
            };

            grid.Clicked += cell =>
            {
                if (drawn == 0) return;

                int orientation = Math.Max(facing.SelectedIndex, 0);

                if (chosen != null)
                {
                    // Moving is a remove plus an add, because the cell is part of what identifies a
                    // placement: the same NPC can stand several times on one map, and 18 of them do.
                    _wanted.Remove(chosen.Key);
                    Put(chosen.Npc, drawn, cell, orientation);
                    chosen = null;
                    list.SelectedItem = null;
                    drop.IsEnabled = false;
                    deselect.IsEnabled = false;
                    Touch();
                    Hint();
                    return;
                }

                if (toPlace == null) return;

                Put(toPlace.Id, drawn, cell, orientation);
                Touch();
            };

            reset.Click += (_, _) =>
            {
                if (chosen == null) return;

                var was = Moved(chosen.Npc);
                if (was == null) return;

                // Put back means exactly that: whatever this NPC's authored rows say, drop them and
                // let the measured placements stand again.
                foreach (var key in _wanted.Keys.Where(k => k.NpcId == chosen.Npc).ToList())
                {
                    if (!_generated.ContainsKey(key)) _wanted.Remove(key);
                }

                foreach (var pair in _generated.Where(p => p.Key.NpcId == chosen.Npc))
                {
                    _wanted[pair.Key] = pair.Value;
                }

                chosen = null;
                list.SelectedItem = null;
                drop.IsEnabled = false;
                reset.IsEnabled = false;
                deselect.IsEnabled = false;
                Touch();
                Hint();
            };

            resetAll.Click += (_, _) =>
            {
                _wanted.Clear();
                foreach (var pair in _generated) _wanted[pair.Key] = pair.Value;

                chosen = null;
                list.SelectedItem = null;
                drop.IsEnabled = false;
                reset.IsEnabled = false;
                deselect.IsEnabled = false;
                Touch();
                Hint();
            };

            drop.Click += (_, _) =>
            {
                if (chosen == null) return;

                _wanted.Remove(chosen.Key);
                chosen = null;
                list.SelectedItem = null;
                drop.IsEnabled = false;
                deselect.IsEnabled = false;
                Touch();
                Hint();
            };

            deselect.Click += (_, _) =>
            {
                chosen = null;
                toPlace = null;
                list.SelectedItem = null;
                Describe(null);
                Picker.Clear(picker);
                drop.IsEnabled = false;
                deselect.IsEnabled = false;
                Redraw();
                Hint();
            };

            search.TextChanged += (_, _) => Show();

            save.Click += (_, _) =>
            {
                try
                {
                    var (rows, removed) = NpcSpawnContent.Delta(_generated, _wanted);
                    NpcSpawnContent.Save(Paths.ContentFile(NpcSpawnContent.AuthoredFile), rows, removed);
                    _world.ReloadNpcPlacements();
                    _dirty = false;
                    Show();
                    status.Text += "   ·   " + Words.T("common.saved");
                }
                catch (Exception ex)
                {
                    status.Text = Words.T("common.couldNotSave", ex.Message);
                }
            };

            var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            foreach (var control in new Control[] { save, search, picker, facing, drop, reset,
                                                    resetAll, deselect, view, status })
            {
                control.Margin = new Thickness(0, 0, 12, 6);
                if (control is not TextBox) control.VerticalAlignment = VerticalAlignment.Center;
                top.Children.Add(control);
            }

            var above = new WrapPanel();
            mapField.Margin = new Thickness(0, 0, 12, 6);
            above.Children.Add(mapField);
            above.Children.Add(Zoomer.For(grid));

            var board = new DockPanel { LastChildFill = true, Margin = new Thickness(12, 0, 0, 0) };
            DockPanel.SetDock(above, Dock.Top);
            hint.Margin = new Thickness(0, 6, 0, 8);
            DockPanel.SetDock(hint, Dock.Top);

            var key = Legend();
            key.Margin = new Thickness(0, 8, 0, 0);
            DockPanel.SetDock(key, Dock.Bottom);

            board.Children.Add(above);
            board.Children.Add(hint);
            board.Children.Add(key);
            board.Children.Add(new ScrollViewer
            {
                Content = grid,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            });

            // The list on the left with the card under it, and the map taking the rest. The map is
            // what the screen is for; the list is how you find something on it.
            var whatItIs = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,10,*") };
            Grid.SetColumn(portrait, 0);
            var cardScroll = new ScrollViewer { Content = card };
            Grid.SetColumn(cardScroll, 2);
            whatItIs.Children.Add(portrait);
            whatItIs.Children.Add(cardScroll);

            var left = new Grid { RowDefinitions = new RowDefinitions("*,10,250") };
            Grid.SetRow(list, 0);
            var cardBox = Skin.Card(whatItIs, 10);
            Grid.SetRow(cardBox, 2);
            left.Children.Add(list);
            left.Children.Add(cardBox);

            var split = new Grid { ColumnDefinitions = new ColumnDefinitions("470,8,*") };
            Grid.SetColumn(left, 0);
            Grid.SetColumn(board, 2);
            split.Children.Add(left);
            split.Children.Add(board);

            var layout = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(top, Dock.Top);
            layout.Children.Add(top);
            layout.Children.Add(split);

            Show();
            Hint();
            Redraw();
            return layout;
        }

        private void Load()
        {
            _npcNames = new NpcCatalogue(_world.Text, _world.Complaints.Add);
            _details = new NpcDetails(_world.Text, _world.Complaints.Add);
            _monsters = new MonsterCatalogue(_world.Text, _world.Complaints.Add);
            _maps = new MapCatalogue(_world.Text, _world.Complaints.Add, _world.NpcsPerMap());

            _all = _npcNames.Ready ? _npcNames.All() : new List<NpcSummary>();
            _gfx = _monsters.Ready ? _monsters.GfxByMonster() : new Dictionary<int, int>();
            _byId = new Dictionary<int, string>();
            _looks = new Dictionary<int, string>();
            foreach (var npc in _all)
            {
                _byId[npc.Id] = npc.Name;
                _looks[npc.Id] = npc.Look;
            }

            // The two regenerable layers without the authored one on top, which is what the delta
            // is worked out against. Read separately from the merged store on purpose: the merged
            // one cannot say which rows came from where once an authored row has replaced one.
            var generated = NpcSpawnContent.Load(Paths.WorldNpcsJson, null, null,
                                                 Paths.WorldNpcsDerivedJson);
            foreach (var pair in generated.Rows)
            {
                _generated[pair.Key] = pair.Value.Value;
                if (pair.Value.From.Layer == ContentLayer.Base) _guessed.Add(pair.Key);
            }

            foreach (var pair in _world.NpcPlacements.Rows) _wanted[pair.Key] = pair.Value.Value;

            _loaded = true;
        }

        private int Gfx(int monsterId) => _gfx.TryGetValue(monsterId, out int id) ? id : 0;

        private string LookOf(int npcId) => _looks.TryGetValue(npcId, out string? look) ? look : "";

        /// <summary>
        /// Where the captures put this NPC, when that is not where it is now.
        /// </summary>
        /// <remarks>
        /// Asked per NPC rather than per placement because a move is a remove plus an add — the
        /// cell is part of what identifies a placement — so after moving one there is no row left
        /// that knows where it came from. The measured layer does.
        /// </remarks>
        private NpcSpawnKey? Moved(int npcId)
        {
            var wasThere = new List<NpcSpawnKey>();
            foreach (var pair in _generated)
            {
                if (pair.Key.NpcId == npcId) wasThere.Add(pair.Key);
            }

            if (wasThere.Count == 0) return null;

            bool allStillThere = wasThere.TrueForAll(key => _wanted.ContainsKey(key));
            int nowHasRows = _wanted.Keys.Count(key => key.NpcId == npcId);

            return allStillThere && nowHasRows == wasThere.Count ? null : wasThere[0];
        }

        private string Name(int npcId)
            => _byId.TryGetValue(npcId, out string? name) && name.Length > 0 ? name : npcId.ToString();

        private static string Short(string name)
            => name.Length <= 14 ? name : name[..13] + "…";

        private void Put(int npcId, long mapId, int cell, int orientation)
        {
            var key = new NpcSpawnKey(mapId, npcId, cell);
            _wanted[key] = new NpcSpawn
            {
                MapId = mapId,
                NpcId = npcId,
                Cell = cell,
                Orientation = orientation,
            };
        }

        private List<Row> Rows()
        {
            var rows = new List<Row>(_wanted.Count);
            foreach (var pair in _wanted)
            {
                bool generated = _generated.TryGetValue(pair.Key, out var was);
                string from = !generated
                    ? Words.T("common.authored")
                    : was.Orientation != pair.Value.Orientation ? Words.T("npc.reFaced")
                    : _guessed.Contains(pair.Key) ? Words.T("common.derived")
                    : Words.T("common.measured");

                rows.Add(new Row(pair.Key, pair.Value.MapId, pair.Value.NpcId, Name(pair.Value.NpcId),
                                 pair.Value.Cell, pair.Value.Orientation, from, !generated));
            }

            rows.Sort((a, b) =>
            {
                int byMap = a.Map.CompareTo(b.Map);
                return byMap != 0 ? byMap : a.Cell.CompareTo(b.Cell);
            });

            return rows;
        }

        private static Control Legend()
        {
            var row = new WrapPanel();
            row.Children.Add(Skin.Key(Skin.MeasuredBrush, Words.T("common.measured")));
            row.Children.Add(Skin.Key(Skin.AuthoredBrush, Words.T("common.authored")));
            row.Children.Add(Skin.Key(Skin.WrongBrush, Words.T("cells.mobHere")));
            return row;
        }

        private static Control Line(Row row)
        {
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("104,58,*,50,38,110") };

            Add(line, 0, row.Map.ToString(), Skin.TextFaintBrush);
            Add(line, 1, row.Npc.ToString(), Skin.TextFaintBrush);

            var name = new TextBlock
            {
                Text = row.Name,
                Foreground = Skin.TextBrush,
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(name, 2);
            line.Children.Add(name);

            Add(line, 3, row.Cell.ToString(), Skin.TextBrush);
            Add(line, 4, row.Facing.ToString(), Skin.TextFaintBrush);
            Add(line, 5, row.From, row.Authored ? Skin.AuthoredBrush : Skin.MeasuredBrush);
            return line;
        }

        private static void Add(Grid line, int column, string text, IBrush colour)
        {
            var block = Skin.Fixed(text, colour);
            Grid.SetColumn(block, column);
            line.Children.Add(block);
        }
    }
}
