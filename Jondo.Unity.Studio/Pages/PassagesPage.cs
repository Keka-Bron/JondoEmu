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
using Jondo.Unity.World.Maps;

namespace Jondo.Unity.Studio.Pages
{
    /// <summary>
    /// Joining two maps: the screen that makes a house with its own interior possible.
    /// </summary>
    /// <remarks>
    /// Two maps side by side, an element picked on each, and one button that ties them together
    /// <em>both ways</em>. The return leg is the point: a door you can walk through and not back out
    /// of is a trap.
    ///
    /// It is offered rather than forced, because one-way passages are real: 529 of the 3,815
    /// extracted rows have no return, and Ankama's own navigation graph has 1,906 one-way
    /// interactive transitions out of 5,719.
    ///
    /// <b>A correction, since this project has repeated the wrong number several times.</b> The
    /// claim that "1,010 of 1,124 missing passages were discarded for having no return element" is
    /// not reproducible from any code in this repository — the extractor's own counter today says
    /// 1,357 of 3,644 — and the rule behind it was doing something else entirely: the return
    /// element was never a requirement for the passage, it was used to GUESS where the passage put
    /// you down. That guess is wrong 96.9% of the time and lands the player on a cell they cannot
    /// stand on in 93.4% of cases.
    ///
    /// <b>An element cannot be invented.</b> The client draws interactive elements from its own map
    /// data, so this screen offers what each map already has — 46,309 elements over 9,840 maps —
    /// and refuses to put a door where there is nothing to click. That is not a limitation of the
    /// editor; it is the shape of the problem, and an editor that let you write one anyway would be
    /// writing a passage nobody could ever use.
    /// </remarks>
    public sealed class PassagesPage : IStudioPage
    {
        private readonly WorldData _world;

        /// <summary>What the world should look like when this is saved.</summary>
        private readonly Dictionary<PassageKey, Passage> _authored = new Dictionary<PassageKey, Passage>();

        /// <summary>Extracted passages a person has taken away.</summary>
        private readonly HashSet<PassageKey> _removed = new HashSet<PassageKey>();

        private InteractiveCatalogue? _elements;
        private MapCatalogue? _maps;
        private bool _loaded;
        private bool _dirty;

        public PassagesPage(WorldData world) => _world = world;

        public string TitleKey => "nav.passages";

        public override string ToString() => Words.T(TitleKey);

        /// <summary>One side of the screen: a map, its grid, its elements.</summary>
        private sealed class Side
        {
            public MapField Field = null!;
            public CellGrid Grid = null!;
            public ListBox List = null!;
            public long MapId;
            public MapElement? Chosen;

            /// <summary>Where the author says to land, or minus one for the worked-out default.</summary>
            public int Landing = -1;
        }

        public Control Build()
        {
            if (!_loaded) Load();

            if (_elements == null || !_elements.Ready)
            {
                var missing = new StackPanel { Spacing = 8 };
                missing.Children.Add(Skin.Heading(Words.T("tp.noElements")));
                missing.Children.Add(OverviewPage.Mono(
                    Words.T("missing.lookedIn", Paths.InteractiveElementsJson)));
                return Skin.Card(missing);
            }

            var status = new TextBlock { Foreground = Skin.TextSoftBrush, VerticalAlignment = VerticalAlignment.Center };
            var save = new Button { Content = Words.T("common.save"), IsEnabled = _dirty };
            var complaints = new TextBlock
            {
                Foreground = Skin.WrongBrush,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
            };

            var tie = new Button { Content = Words.T("tp.tie"), IsEnabled = false };
            var oneWay = new Button { Content = Words.T("tp.oneWay"), IsEnabled = false };
            var cut = new Button { Content = Words.T("tp.cut"), IsEnabled = false };
            var hint = new TextBlock { Foreground = Skin.TextSoftBrush, TextWrapping = TextWrapping.Wrap };

            var here = NewSide();
            var there = NewSide();

            // ─── what is on each half ─────────────────────────────────────────────

            void Draw(Side side, Side other)
            {
                side.Grid.Show(side.MapId != 0 && _world.Maps.TryGetValue(side.MapId, out var cells)
                    ? cells : null);

                var marks = new Dictionary<int, CellMark>();
                foreach (var element in _elements.On(side.MapId))
                {
                    marks[element.Cell] = new CellMark
                    {
                        Colour = Colour(element),
                        Label = Label(element, other.MapId),
                    };
                }

                if (side.Landing >= 0 && !marks.ContainsKey(side.Landing))
                {
                    marks[side.Landing] = new CellMark
                    {
                        Colour = Skin.MeasuredBrush,
                        Label = Words.T("tp.landsHere"),
                    };
                }

                side.Grid.Mark(marks);
                side.Grid.Select(side.Chosen?.Cell ?? -1);
                side.List.ItemsSource = _elements.On(side.MapId).ToList();
            }

            void Say()
            {
                var text = new StringBuilder();
                text.Append(Words.T("tp.counts", _elements.ElementCount.ToString("N0"),
                                    _elements.MapCount.ToString("N0"),
                                    _elements.ExtractedCount.ToString("N0")));

                if (_authored.Count > 0 || _removed.Count > 0)
                {
                    text.Append("   ·   ").Append(Words.T("tp.mine", _authored.Count, _removed.Count));
                }

                if (_dirty) text.Append("   ·   ").Append(Words.T("common.unsaved"));
                status.Text = text.ToString();
                save.IsEnabled = _dirty;

                complaints.Text = string.Join(Environment.NewLine,
                    TeleportContent.Complaints(_authored.Values));
            }

            void Hint()
            {
                if (here.MapId == 0 || there.MapId == 0) { hint.Text = Words.T("tp.pickTwoMaps"); return; }
                if (here.Chosen == null || there.Chosen == null) { hint.Text = Words.T("tp.pickTwoDoors"); return; }

                hint.Text = Words.T("tp.ready",
                                    here.Chosen.Cell, here.MapId,
                                    there.Chosen.Cell, there.MapId)
                          + "   ·   " + Words.T("tp.lands",
                                                LandingFor(there.Chosen, there.Landing),
                                                LandingFor(here.Chosen, here.Landing));
            }

            void Refresh()
            {
                _elements.Apply(_authored, _removed);
                Draw(here, there);
                Draw(there, here);

                bool both = here.Chosen != null && there.Chosen != null;
                tie.IsEnabled = both;
                oneWay.IsEnabled = both;
                cut.IsEnabled = here.Chosen is { IsPassage: true } || there.Chosen is { IsPassage: true };

                Say();
                Hint();
            }

            void Wire(Side side, Side other)
            {
                side.Field.Chosen += place =>
                {
                    side.MapId = place.MapId;
                    side.Chosen = null;
                    side.Landing = -1;
                    Refresh();
                };

                side.List.SelectionChanged += (_, _) =>
                {
                    side.Chosen = side.List.SelectedItem as MapElement;
                    Refresh();
                };

                side.Grid.Clicked += cell =>
                {
                    // An element on that cell means "pick that door", which is how anybody would
                    // expect a map to behave. An empty cell means "land here" — the worked-out
                    // default is right about half the time, so overriding it has to be one click.
                    var on = _elements.On(side.MapId).FirstOrDefault(e => e.Cell == cell);
                    if (on != null)
                    {
                        side.List.SelectedItem = on;
                        return;
                    }

                    side.Landing = side.Landing == cell ? -1 : cell;
                    Refresh();
                };
            }

            Wire(here, there);
            Wire(there, here);

            // ─── tying them together ──────────────────────────────────────────────

            void Join(bool bothWays)
            {
                if (here.Chosen == null || there.Chosen == null) return;

                Put(here.Chosen, there.Chosen, there.Landing);
                if (bothWays) Put(there.Chosen, here.Chosen, here.Landing);

                _dirty = true;
                Refresh();
            }

            tie.Click += (_, _) => Join(bothWays: true);
            oneWay.Click += (_, _) => Join(bothWays: false);

            cut.Click += (_, _) =>
            {
                foreach (var side in new[] { here, there })
                {
                    if (side.Chosen is not { IsPassage: true }) continue;

                    var key = new PassageKey(side.Chosen.MapId, side.Chosen.ElementId);
                    if (side.Chosen.Extracted) _removed.Add(key);
                    else _authored.Remove(key);
                }

                _dirty = true;
                Refresh();
            };

            save.Click += (_, _) =>
            {
                var wrong = TeleportContent.Complaints(_authored.Values).ToList();
                if (wrong.Count > 0)
                {
                    complaints.Text = Words.T("tp.notSaved", string.Join(" ", wrong));
                    return;
                }

                try
                {
                    TeleportContent.Save(Paths.ContentFile(TeleportContent.AuthoredFile),
                                         _authored.Values, _removed);
                    _world.ReloadPassages();
                    _dirty = false;
                    Say();
                    status.Text += "   ·   " + Words.T("common.saved");
                }
                catch (Exception ex)
                {
                    status.Text = Words.T("common.couldNotSave", ex.Message);
                }
            };

            // ─── the shape of the screen ──────────────────────────────────────────

            var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            foreach (var control in new Control[] { save, tie, oneWay, cut, status })
            {
                control.Margin = new Thickness(0, 0, 12, 6);
                control.VerticalAlignment = VerticalAlignment.Center;
                top.Children.Add(control);
            }

            var halves = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
            var left = Half(here, Words.T("tp.from"));
            var right = Half(there, Words.T("tp.to"));
            Grid.SetColumn(left, 0);
            Grid.SetColumn(right, 2);
            halves.Children.Add(left);
            halves.Children.Add(right);

            var bottom = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
            bottom.Children.Add(hint);
            bottom.Children.Add(complaints);
            bottom.Children.Add(Legend());

            var layout = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            layout.Children.Add(top);
            layout.Children.Add(bottom);
            layout.Children.Add(halves);

            Refresh();
            return layout;
        }

        private Side NewSide()
        {
            var side = new Side
            {
                Grid = new CellGrid(),
                Field = new MapField(_maps!, 320),
                List = new ListBox
                {
                    MaxHeight = 190,
                    ItemTemplate = new FuncDataTemplate<MapElement>((element, _) => ElementRow(element),
                                                                    supportsRecycling: true),
                },
            };

            ScrollViewer.SetHorizontalScrollBarVisibility(side.List, ScrollBarVisibility.Disabled);
            return side;
        }

        private static Control Half(Side side, string title)
        {
            var panel = new DockPanel { LastChildFill = true };

            var heading = Skin.Heading(title);
            heading.Margin = new Thickness(0, 0, 0, 6);
            DockPanel.SetDock(heading, Dock.Top);

            var above = new WrapPanel();
            side.Field.Margin = new Thickness(0, 0, 10, 6);
            above.Children.Add(side.Field);
            above.Children.Add(Zoomer.For(side.Grid, 1.25));
            DockPanel.SetDock(above, Dock.Top);

            side.List.Margin = new Thickness(0, 8, 0, 0);
            DockPanel.SetDock(side.List, Dock.Bottom);

            panel.Children.Add(heading);
            panel.Children.Add(above);
            panel.Children.Add(side.List);
            panel.Children.Add(new ScrollViewer
            {
                Content = side.Grid,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            });

            return panel;
        }

        /// <summary>Writes one leg of a passage.</summary>
        private void Put(MapElement from, MapElement to, int landing)
        {
            var key = new PassageKey(from.MapId, from.ElementId);

            _removed.Remove(key);
            _authored[key] = new Passage
            {
                SourceMapId = from.MapId,
                ElementId = from.ElementId,
                SourceCell = from.Cell,
                GfxId = from.GfxId,

                // The type measured for this drawing when there is one. Type 0 is not a fallback:
                // it appears zero times in the 154 types observed across the captures.
                InteractiveType = from.MeasuredType > 0 ? from.MeasuredType : TeleportContent.DefaultType,
                SkillId = TeleportContent.DefaultSkill,
                DestinationMapId = to.MapId,
                DestinationCell = LandingFor(to, landing),
            };
        }

        /// <summary>
        /// Where a passage puts you down.
        /// </summary>
        /// <remarks>
        /// A walkable cell NEXT TO the door, not the door's own cell. Measured over the 1,294
        /// passages whose arrival was really observed rather than guessed:
        ///
        /// <code>
        ///   the element's own cell         2.6% right     (and element cells are blocking
        ///                                                  in 79% of the game)
        ///   nearest walkable cell         32.6%
        ///   first walkable neighbour   41 - 47%           depending on which order
        ///   any walkable-neighbour rule   57.2% ceiling
        /// </code>
        ///
        /// So this is a <em>default</em> and never a rule: a click on the far map overrides it, and
        /// nothing here refuses an arrival cell somebody chose. Hardening it into a validation
        /// would reject 353 of those 1,294 real passages.
        ///
        /// One data limit worth knowing: <c>map_walkable_cells.json</c> only covers cells 86 to
        /// 487, so a door in the top or bottom five rows of a map has no walkable neighbour on
        /// record and falls back to its own cell.
        /// </remarks>
        private int LandingFor(MapElement to, int chosen)
        {
            if (chosen >= 0) return chosen;
            if (!_world.Maps.TryGetValue(to.MapId, out var cells)) return to.Cell;

            foreach (int neighbour in MapGeometry.GetNeighbors(to.Cell))
            {
                if (cells.Walkable.Contains(neighbour)) return neighbour;
            }

            return to.Cell;
        }

        private void Load()
        {
            _elements = new InteractiveCatalogue(_world.Complaints.Add);
            _maps = new MapCatalogue(_world.Text, _world.Complaints.Add, _world.NpcsPerMap());

            foreach (var pair in _world.Passages.Rows) _authored[pair.Key] = pair.Value.Value;
            foreach (var key in _world.Passages.ErasedKeys) _removed.Add(key);

            _loaded = true;
        }

        private static IBrush Colour(MapElement element)
        {
            if (!element.IsPassage) return new SolidColorBrush(Color.FromArgb(0x70, 0x9A, 0xA2, 0xB0));
            return element.Extracted ? Skin.DoneBrush : Skin.AuthoredBrush;
        }

        private static string Label(MapElement element, long otherMap)
        {
            if (!element.IsPassage) return "";

            var leads = element.Leads!.Value;

            // A passage that goes to the map on the other half of the screen is the one being
            // worked on, and saying so saves reading eight-digit numbers off two lists.
            return leads.DestinationMapId == otherMap && otherMap != 0
                ? "⇄"
                : "→ " + leads.DestinationMapId;
        }

        private static Control Legend()
        {
            var row = new WrapPanel();
            row.Children.Add(Skin.Key(new SolidColorBrush(Color.FromArgb(0x70, 0x9A, 0xA2, 0xB0)),
                                      Words.T("tp.free")));
            row.Children.Add(Skin.Key(Skin.DoneBrush, Words.T("tp.extracted")));
            row.Children.Add(Skin.Key(Skin.AuthoredBrush, Words.T("common.authored")));
            return row;
        }

        private static Control ElementRow(MapElement element)
        {
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("48,74,76,*") };

            Cell(line, 0, element.Cell.ToString(), Skin.TextBrush);
            Cell(line, 1, "gfx " + element.GfxId, Skin.TextFaintBrush);
            Cell(line, 2,
                 element.IsPassage
                     ? (element.Extracted ? Words.T("tp.extracted") : Words.T("common.authored"))
                     : Words.T("tp.free"),
                 element.IsPassage
                     ? (element.Extracted ? Skin.DoneBrush : Skin.AuthoredBrush)
                     : Skin.TextFaintBrush);

            Cell(line, 3,
                 element.IsPassage
                     ? $"→ {element.Leads!.Value.DestinationMapId} @ {element.Leads!.Value.DestinationCell}"
                     : (element.MeasuredType > 0 ? Words.T("tp.measuredType", element.MeasuredType) : ""),
                 Skin.TextSoftBrush);

            return line;
        }

        private static void Cell(Grid line, int column, string text, IBrush colour)
        {
            var block = Skin.Fixed(text, colour);
            Grid.SetColumn(block, column);
            line.Children.Add(block);
        }
    }
}
