using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    /// One map's 560 cells, and the paint that changes them.
    /// </summary>
    /// <remarks>
    /// Three layers, and none of them follows from the others: a cell can be walked on outside a
    /// fight and blocked inside one, and a cell can be seen through without being walkable. So the
    /// brush paints one layer at a time and says which.
    ///
    /// What is saved is the <em>difference</em> — three cells, not 560. See
    /// <see cref="CellContent"/> for why that rule is load-bearing.
    ///
    /// Two things worth knowing before deciding a cell is wrong. The generated files disagree on
    /// purpose: <c>map_walkable_cells.json</c> trims the map's outer ring so that monsters are not
    /// placed there during roleplay, which is why a border cell often reads as blocked while the
    /// fight file says it is fine. And the decor — the couple of thousand drawn elements on a map —
    /// is not here and is a project of its own; this screen paints what the server believes, not
    /// what the map looks like.
    /// </remarks>
    public sealed class MapCellsPage : IStudioPage
    {
        private readonly WorldData _world;
        private readonly MonsterIcons _icons = new MonsterIcons();
        private readonly NpcSprites _sprites = new NpcSprites();

        /// <summary>The changes, by cell. What gets written.</summary>
        private readonly Dictionary<CellKey, CellPatch> _patches = new Dictionary<CellKey, CellPatch>();

        private MapCatalogue? _maps;
        private MonsterCatalogue? _monsters;
        private NpcCatalogue? _npcs;
        private Dictionary<int, string> _npcNames = new Dictionary<int, string>();
        private Dictionary<int, string> _npcLooks = new Dictionary<int, string>();
        private Dictionary<int, int> _gfx = new Dictionary<int, int>();

        private bool _loaded;
        private bool _dirty;

        public MapCellsPage(WorldData world) => _world = world;

        public string TitleKey => "nav.cells";

        public override string ToString() => Words.T(TitleKey);

        /// <summary>Which of the three the brush is painting.</summary>
        private enum Layer
        {
            Walkable = 0,
            InFight = 1,
            Sight = 2,
        }

        public Control Build()
        {
            if (!_loaded) Load();

            var grid = new CellGrid();
            var mapField = new MapField(_maps!, 340);
            var counts = new TextBlock { Foreground = Skin.TextSoftBrush, VerticalAlignment = VerticalAlignment.Center };
            var under = new TextBlock
            {
                Foreground = Skin.TextBrush,
                FontFamily = Skin.Mono,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var save = new Button { Content = Words.T("common.save"), IsEnabled = _dirty };
            var brush = new ToggleButton { Content = Words.T("cells.look"), MinWidth = 128 };
            var layer = new ComboBox
            {
                Width = 190,
                ItemsSource = new[]
                {
                    Words.T("cells.walkable"),
                    Words.T("cells.inFight"),
                    Words.T("cells.blocksSight"),
                },
                SelectedIndex = 0,
                IsEnabled = false,
            };

            var undoMap = new Button { Content = Words.T("cells.undoMap"), IsEnabled = false };
            var undoAll = new Button { Content = Words.T("cells.undoAll"), IsEnabled = _patches.Count > 0 };
            var view = new ToggleButton { Content = Words.T("common.editView"), MinWidth = 132 };

            // Declared here and not down with the rest of the layout: ShowNeighbours touches them
            // and it is reached from Open, which the compiler sees first.
            var north = new Button { Content = "↑", MinWidth = 44 };
            var west = new Button { Content = "←", MinWidth = 44 };
            var east = new Button { Content = "→", MinWidth = 44 };
            var south = new Button { Content = "↓", MinWidth = 44 };

            long drawn = 0;
            bool painting = false;
            bool paintTo = true;

            // ─── what is on the map ───────────────────────────────────────────────

            MapCells Now(long mapId)
            {
                _world.Maps.TryGetValue(mapId, out var cells);
                var shown = new MapCells();

                if (cells != null)
                {
                    foreach (int cell in cells.Walkable) shown.Walkable.Add(cell);
                    foreach (int cell in cells.WalkableInFight) shown.WalkableInFight.Add(cell);
                    foreach (int cell in cells.SightBlockers) shown.SightBlockers.Add(cell);
                }

                // The patches laid on top by the same code the server uses, so the two cannot drift.
                var walk = new Dictionary<long, HashSet<int>> { [mapId] = shown.Walkable };
                var fight = new Dictionary<long, HashSet<int>> { [mapId] = shown.WalkableInFight };
                var sight = new Dictionary<long, HashSet<int>> { [mapId] = shown.SightBlockers };

                CellContent.Apply(_patches.Values.Where(p => p.MapId == mapId), walk, fight, sight);
                return shown;
            }

            void Redraw()
            {
                var cells = drawn == 0 ? null : Now(drawn);
                grid.Show(cells);

                var marks = new Dictionary<int, CellMark>();

                if (drawn != 0)
                {
                    foreach (var pair in _world.NpcPlacements.Rows)
                    {
                        if (pair.Key.MapId != drawn) continue;
                        marks[pair.Key.Cell] = new CellMark
                        {
                            Colour = Skin.MeasuredBrush,
                            Label = Short(_npcNames.TryGetValue(pair.Key.NpcId, out string? name) && name.Length > 0
                                ? name
                                : pair.Key.NpcId.ToString()),
                            Icon = _npcLooks.TryGetValue(pair.Key.NpcId, out string? look)
                                ? _sprites.Of(look) : null,
                        };
                    }

                    if (_monsters is { Ready: true })
                    {
                        foreach (var group in _monsters.GroupsOn(drawn))
                        {
                            marks[group.Cell] = new CellMark
                            {
                                Colour = Skin.WrongBrush,
                                Label = "×" + group.Members.Count,
                                Icon = group.Members.Count > 0 && _gfx.TryGetValue(group.Members[0].Monster, out int gfx)
                                    ? _icons.Of(gfx) : null,
                            };
                        }
                    }

                    // What has been changed, drawn hollow so the ground underneath still reads.
                    foreach (var patch in _patches.Values)
                    {
                        if (patch.MapId != drawn || marks.ContainsKey(patch.Cell)) continue;

                        marks[patch.Cell] = new CellMark
                        {
                            Colour = Skin.AuthoredBrush,
                            Faded = true,
                        };
                    }
                }

                grid.Mark(marks);

                int mine = _patches.Values.Count(p => p.MapId == drawn);
                counts.Text = drawn == 0 || cells == null
                    ? Words.T("maps.nothingHere")
                    : Words.T("cells.counts", cells.Walkable.Count, cells.WalkableInFight.Count,
                              cells.SightBlockers.Count)
                      + (mine > 0 ? "   ·   " + Words.T("cells.changed", mine) : "")
                      + (_dirty ? "   ·   " + Words.T("common.unsaved") : "");

                save.IsEnabled = _dirty;
                undoMap.IsEnabled = mine > 0;
                undoAll.IsEnabled = _patches.Count > 0;
            }

            // ─── painting ─────────────────────────────────────────────────────────

            bool Reads(long mapId, int cell, Layer which)
            {
                var cells = Now(mapId);
                return which switch
                {
                    Layer.InFight => cells.WalkableInFight.Contains(cell),
                    Layer.Sight => cells.SightBlockers.Contains(cell),
                    _ => cells.Walkable.Contains(cell),
                };
            }

            void Paint(int cell, bool to)
            {
                var which = (Layer)Math.Max(layer.SelectedIndex, 0);
                var key = new CellKey(drawn, cell);

                _patches.TryGetValue(key, out var patch);
                patch = which switch
                {
                    Layer.InFight => new CellPatch
                    {
                        MapId = drawn, Cell = cell,
                        Walkable = patch.Walkable, WalkableInFight = to, BlocksSight = patch.BlocksSight,
                    },
                    Layer.Sight => new CellPatch
                    {
                        MapId = drawn, Cell = cell,
                        Walkable = patch.Walkable, WalkableInFight = patch.WalkableInFight, BlocksSight = to,
                    },
                    _ => new CellPatch
                    {
                        MapId = drawn, Cell = cell,
                        Walkable = to, WalkableInFight = patch.WalkableInFight, BlocksSight = patch.BlocksSight,
                    },
                };

                _patches[key] = patch;
                _dirty = true;
            }

            grid.Painted += cell =>
            {
                if (!painting || drawn == 0) return;

                if (!_paintingRun)
                {
                    // The first cell decides which way the whole stroke goes, which is how every
                    // paint tool anybody has ever used behaves.
                    paintTo = !Reads(drawn, cell, (Layer)Math.Max(layer.SelectedIndex, 0));
                    _paintingRun = true;
                }

                Paint(cell, paintTo);
                Redraw();
            };

            grid.Clicked += _ => _paintingRun = false;

            grid.HoveredChanged += cell =>
            {
                if (cell < 0 || drawn == 0)
                {
                    under.Text = "";
                    _paintingRun = false;
                    return;
                }

                var cells = Now(drawn);
                var what = new List<string>();
                if (cells.Walkable.Contains(cell)) what.Add(Words.T("cells.walkable"));
                if (cells.WalkableInFight.Contains(cell)) what.Add(Words.T("cells.inFight"));
                if (cells.SightBlockers.Contains(cell)) what.Add(Words.T("cells.blocksSight"));
                if (what.Count == 0) what.Add(Words.T("cells.solid"));

                under.Text = $"{cell}   {string.Join(" · ", what)}";
            };

            // ─── the controls ─────────────────────────────────────────────────────

            void Open(long mapId)
            {
                drawn = mapId;
                mapField.Set(mapId);
                Redraw();
                ShowNeighbours();
            }

            mapField.Chosen += place => Open(place.MapId);

            brush.IsCheckedChanged += (_, _) =>
            {
                painting = brush.IsChecked == true;
                layer.IsEnabled = painting;
                brush.Content = painting ? Words.T("cells.painting") : Words.T("cells.look");
            };

            layer.SelectionChanged += (_, _) => _paintingRun = false;

            view.IsCheckedChanged += (_, _) =>
            {
                bool game = view.IsChecked == true;
                grid.View = game ? GridView.Game : GridView.Editing;
                view.Content = game ? Words.T("common.gameView") : Words.T("common.editView");
            };

            undoMap.Click += (_, _) =>
            {
                foreach (var key in _patches.Keys.Where(k => k.MapId == drawn).ToList()) _patches.Remove(key);
                _dirty = true;
                Redraw();
            };

            undoAll.Click += (_, _) =>
            {
                _patches.Clear();
                _dirty = true;
                Redraw();
            };

            save.Click += (_, _) =>
            {
                try
                {
                    CellContent.Save(Paths.ContentFile(CellContent.AuthoredFile), _patches.Values);
                    _world.ReloadCellPatches();
                    _dirty = false;
                    Redraw();
                    counts.Text += "   ·   " + Words.T("common.saved");
                }
                catch (Exception ex)
                {
                    counts.Text = Words.T("common.couldNotSave", ex.Message);
                }
            };

            // ─── the four maps around this one ────────────────────────────────────

            void ShowNeighbours()
            {
                var (top, right, bottom, left) = _maps!.Around(drawn);
                north.IsEnabled = top != 0;
                east.IsEnabled = right != 0;
                south.IsEnabled = bottom != 0;
                west.IsEnabled = left != 0;

                north.Tag = top;
                east.Tag = right;
                south.Tag = bottom;
                west.Tag = left;
            }

            foreach (var button in new[] { north, west, east, south })
            {
                button.Click += (sender, _) =>
                {
                    if (sender is Button pressed && pressed.Tag is long to && to != 0) Open(to);
                };
            }

            var compass = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto"),
                RowDefinitions = new RowDefinitions("Auto,Auto"),
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            Grid.SetColumn(north, 1);
            Grid.SetRow(west, 1);
            Grid.SetColumn(south, 1);
            Grid.SetRow(south, 1);
            Grid.SetColumn(east, 2);
            Grid.SetRow(east, 1);
            compass.Children.Add(north);
            compass.Children.Add(west);
            compass.Children.Add(south);
            compass.Children.Add(east);

            var top2 = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            foreach (var control in new Control[] { save, brush, layer, undoMap, undoAll, view, counts, under })
            {
                control.Margin = new Thickness(0, 0, 12, 6);
                control.VerticalAlignment = VerticalAlignment.Center;
                top2.Children.Add(control);
            }

            var side = new StackPanel { Spacing = 10, Width = 340 };
            side.Children.Add(mapField);
            side.Children.Add(Zoomer.For(grid, 1.8));
            side.Children.Add(Skin.Label(Words.T("cells.around")));
            side.Children.Add(compass);
            side.Children.Add(Legend());
            side.Children.Add(new TextBlock
            {
                Text = Words.T("cells.trimmed"),
                Foreground = Skin.TextFaintBrush,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            });

            var board = new ScrollViewer
            {
                Content = grid,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };

            var split = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,14,*") };
            Grid.SetColumn(side, 0);
            Grid.SetColumn(board, 2);
            split.Children.Add(side);
            split.Children.Add(board);

            var layout = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(top2, Dock.Top);
            layout.Children.Add(top2);
            layout.Children.Add(split);

            ShowNeighbours();
            Redraw();
            return layout;
        }

        /// <summary>True while one stroke is in progress, so it all goes the same way.</summary>
        private bool _paintingRun;

        private void Load()
        {
            _maps = new MapCatalogue(_world.Text, _world.Complaints.Add, _world.NpcsPerMap());
            _monsters = new MonsterCatalogue(_world.Text, _world.Complaints.Add);
            _npcs = new NpcCatalogue(_world.Text, _world.Complaints.Add);
            _gfx = _monsters.Ready ? _monsters.GfxByMonster() : new Dictionary<int, int>();

            if (_npcs.Ready)
            {
                foreach (var npc in _npcs.All())
                {
                    _npcNames[npc.Id] = npc.Name;
                    _npcLooks[npc.Id] = npc.Look;
                }
            }

            foreach (var pair in _world.CellPatches.Rows) _patches[pair.Key] = pair.Value.Value;

            _loaded = true;
        }

        private static string Short(string name) => name.Length <= 14 ? name : name[..13] + "…";

        private static Control Legend()
        {
            var panel = new StackPanel { Spacing = 6 };

            var terrain = new WrapPanel();
            terrain.Children.Add(Skin.Key(new SolidColorBrush(Color.FromRgb(0x3E, 0x6B, 0x4E)), Words.T("cells.walkable")));
            terrain.Children.Add(Skin.Key(new SolidColorBrush(Color.FromRgb(0x7A, 0x50, 0x2A)), Words.T("cells.notInFight")));
            terrain.Children.Add(Skin.Key(new SolidColorBrush(Color.FromRgb(0x2E, 0x42, 0x63)), Words.T("cells.seen")));
            terrain.Children.Add(Skin.Key(new SolidColorBrush(Color.FromRgb(0x22, 0x25, 0x2C)), Words.T("cells.solid")));

            var things = new WrapPanel();
            things.Children.Add(Skin.Key(Skin.MeasuredBrush, Words.T("cells.npcHere")));
            things.Children.Add(Skin.Key(Skin.WrongBrush, Words.T("cells.mobHere")));
            things.Children.Add(Skin.Key(Skin.AuthoredBrush, Words.T("cells.mine")));

            panel.Children.Add(terrain);
            panel.Children.Add(things);
            return Skin.Card(panel, 10);
        }
    }
}
