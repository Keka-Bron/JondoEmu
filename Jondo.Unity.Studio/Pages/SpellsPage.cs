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
using Jondo.Unity.World.Combat;
using Jondo.Unity.World.Maps;

namespace Jondo.Unity.Studio.Pages
{
    /// <summary>
    /// A spell, what it does, and which cells it would hit — aimed with the mouse.
    /// </summary>
    /// <remarks>
    /// The architecture document argues a simulator is worth more than the spell editor itself, and
    /// this is why: reading <c>zoneDescr: { shape: 80, param1: 1 }</c> tells you nothing, and seeing
    /// the four cells light up as you sweep the pointer tells you everything.
    ///
    /// The area is not worked out here. It calls <see cref="Zone.Casillas"/> — the same method the
    /// fight engine calls when a spell actually goes off — so what is on screen is what the server
    /// would do, not a drawing of what it is supposed to do. That is the whole point of the editor
    /// living in the same solution: a second implementation would agree until the day one of them
    /// was fixed.
    ///
    /// It also puts two measured content bugs on screen where they cannot be missed: a spell whose
    /// range is 0–0 can only be cast on the caster's own cell, and 1,555 spells are in that state;
    /// and a shape this build does not know falls back to the centre cell alone, which is why the
    /// shape letter is shown raw when it is not one of the ten <see cref="Zone"/> handles.
    /// </remarks>
    public sealed class SpellsPage : IStudioPage
    {
        private readonly WorldData _world;
        private readonly MonsterIcons _icons = new MonsterIcons();

        private SpellCatalogue? _spells;
        private MonsterCatalogue? _monsters;
        private List<SpellSummary> _allSpells = new List<SpellSummary>();
        private List<MonsterSummary> _allMonsters = new List<MonsterSummary>();
        private bool _loaded;

        public SpellsPage(WorldData world) => _world = world;

        public string TitleKey => "nav.spells";

        public override string ToString() => Words.T(TitleKey);

        /// <summary>
        /// Where the caster stands when the screen opens: row 20, column 7 of a 14 by 40 grid.
        /// </summary>
        /// <remarks>
        /// Middle of the map on purpose. Anywhere on an edge and half of every area falls off the
        /// grid, which makes the first thing anybody sees on this screen a lie about the spell.
        /// </remarks>
        private const int Middle = 287;

        private sealed record SpellRow(int SpellId, int Grade, string Name, SpellLevelInfo? Level);

        public Control Build()
        {
            if (!_loaded) Load();

            if (_spells == null || !_spells.Ready)
            {
                var missing = new StackPanel { Spacing = 8 };
                missing.Children.Add(Skin.Heading(Words.T("missing.world")));
                missing.Children.Add(OverviewPage.Mono(Words.T("missing.lookedIn", Paths.WorldDb)));
                return Skin.Card(missing);
            }

            var spellList = new ListBox
            {
                ItemTemplate = new FuncDataTemplate<SpellRow>((row, _) => SpellLine(row), supportsRecycling: true),
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(spellList, ScrollBarVisibility.Disabled);

            var effectList = new ListBox
            {
                ItemTemplate = new FuncDataTemplate<SpellEffectInfo>((effect, _) => EffectLine(effect),
                                                                     supportsRecycling: true),
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(effectList, ScrollBarVisibility.Disabled);

            var grid = new CellGrid();
            var card = new SelectableTextBlock
            {
                FontFamily = Skin.Mono,
                FontSize = 12.5,
                Foreground = Skin.TextBrush,
                TextWrapping = TextWrapping.Wrap,
            };

            var status = new TextBlock { Foreground = Skin.TextSoftBrush, VerticalAlignment = VerticalAlignment.Center };
            var hint = new TextBlock { Foreground = Skin.TextSoftBrush, TextWrapping = TextWrapping.Wrap };
            var moveCaster = new ToggleButton { Content = Words.T("spell.moveCaster"), MinWidth = 150 };

            var search = new TextBox { Watermark = Words.T("common.search"), Width = 190, FontSize = 12.5 };

            // The work list: which effects have no code, worst first. It is on this screen and not
            // buried somewhere because it is the answer to "why does this spell not do anything",
            // and that question is asked far more often than any other here.
            var showDead = new ToggleButton { Content = Words.T("spell.showDead"), MinWidth = 190 };
            var deadList = new ListBox
            {
                IsVisible = false,
                ItemTemplate = new FuncDataTemplate<SpellCatalogue.DeadEffect>(
                    (dead, _) => DeadRow(dead), supportsRecycling: true),
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(deadList, ScrollBarVisibility.Disabled);

            int caster = Middle;
            int aimed = -1;
            bool frozen = false;
            SpellRow? chosen = null;
            MonsterSummary? whose = null;

            // ─── what is on the grid ──────────────────────────────────────────────

            void Redraw()
            {
                var wash = new Dictionary<int, IBrush>();
                var level = chosen?.Level;

                if (level != null)
                {
                    // Reach first, so the area paints over it.
                    for (int cell = 0; cell < MapGeometry.MaxCells; cell++)
                    {
                        if (cell == caster) continue;

                        int away = MapGeometry.Distance(caster, cell);
                        if (away < level.MinRange || away > level.MaxRange) continue;
                        if (level.CastInLine && !InLine(caster, cell)) continue;

                        wash[cell] = Reach;
                    }

                    if (aimed >= 0 && Reaches(level, caster, aimed))
                    {
                        foreach (var effect in level.Effects)
                        {
                            if (effect.Critical) continue;

                            foreach (int hit in Zone.Casillas(effect.ZoneShape, effect.ZoneSize, caster, aimed))
                            {
                                if (MapGeometry.IsValid(hit)) wash[hit] = Hit;
                            }
                        }
                    }
                }

                grid.Wash(wash);

                var marks = new Dictionary<int, CellMark>
                {
                    [caster] = new CellMark
                    {
                        Colour = Skin.AuthoredBrush,
                        Label = whose?.Name.Length > 0 ? Short(whose.Name) : Words.T("spell.caster"),
                        Icon = whose != null ? _icons.Of(whose.GfxId) : null,
                    },
                };

                grid.Mark(marks);
                grid.Select(aimed);
            }

            void Describe()
            {
                var level = chosen?.Level;
                if (chosen == null || level == null)
                {
                    card.Text = Words.T("spell.pickOne");
                    effectList.ItemsSource = Array.Empty<SpellEffectInfo>();
                    return;
                }

                var text = new StringBuilder();
                text.AppendLine($"{chosen.Name}   ·   {Words.T("spell.grade", level.Grade)}");
                text.AppendLine();
                text.AppendLine($"{Words.T("spell.ap")}      {level.ApCost}");
                text.AppendLine($"{Words.T("spell.range")}   {level.MinRange} – {level.MaxRange}" +
                                (level.OnSelfOnly ? "   ← " + Words.T("spell.selfOnly") : ""));
                if (level.CastInLine) text.AppendLine(Words.T("spell.inLine"));
                if (level.NeedsSight) text.AppendLine(Words.T("spell.needsSight"));
                if (level.MaxPerTurn > 0) text.AppendLine($"{Words.T("spell.perTurn")}  {level.MaxPerTurn}");
                if (level.MaxPerTarget > 0) text.AppendLine($"{Words.T("spell.perTarget")} {level.MaxPerTarget}");

                int dead = level.Effects.Count(e => e.DoesNothing);
                if (dead > 0)
                {
                    text.AppendLine();
                    text.AppendLine(Words.T("spell.deadEffects", dead));
                }

                card.Text = text.ToString();
                effectList.ItemsSource = level.Effects.ToList();
            }

            void Aim(int cell)
            {
                if (frozen) return;
                aimed = cell;
                Redraw();
                Hint();
            }

            void Hint()
            {
                if (chosen == null) { hint.Text = Words.T("spell.pickOne"); return; }
                if (moveCaster.IsChecked == true) { hint.Text = Words.T("spell.clickToMove"); return; }

                var level = chosen.Level;
                if (level == null) { hint.Text = ""; return; }

                if (aimed < 0) { hint.Text = Words.T("spell.sweep"); return; }

                hint.Text = Reaches(level, caster, aimed)
                    ? Words.T("spell.aimedAt", aimed, MapGeometry.Distance(caster, aimed))
                    : Words.T("spell.outOfReach", aimed, MapGeometry.Distance(caster, aimed));
            }

            void ShowSpells()
            {
                var rows = new List<SpellRow>();
                string needle = (search.Text ?? "").Trim();

                if (whose != null)
                {
                    foreach (var (spellId, grade) in _spells.Of(whose.Id))
                    {
                        rows.Add(Row(spellId, grade));
                    }
                }
                else if (needle.Length > 0)
                {
                    foreach (var spell in _allSpells)
                    {
                        if (!spell.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase) &&
                            !spell.Id.ToString().Contains(needle, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        rows.Add(Row(spell.Id, spell.Grades.Count > 0 ? spell.Grades[0] : 1));
                        if (rows.Count >= 120) break;
                    }
                }

                spellList.ItemsSource = rows;

                var seen = _spells!.Coverage;
                status.Text = (whose != null
                        ? Words.T("spell.knows", whose.Name.Length > 0 ? whose.Name : whose.Id.ToString(), rows.Count)
                        : Words.T("spell.inTheGame", _allSpells.Count.ToString("N0"), rows.Count))
                    + "   ·   " + Words.T("spell.coverage", seen.Direct, seen.Characteristic, seen.PanelOnly);
            }

            SpellRow Row(int spellId, int grade)
            {
                var level = _spells!.Level(spellId, grade) ?? _spells.Level(spellId, 1);
                string name = _allSpells.Find(s => s.Id == spellId)?.Name ?? "";
                return new SpellRow(spellId, grade, name.Length > 0 ? name : spellId.ToString(), level);
            }

            // ─── what the controls do ─────────────────────────────────────────────

            var monsterPicker = Picker.Of(_allMonsters,
                                          m => m.Name.Length > 0 ? m.Name : m.Id.ToString(),
                                          m => m.Id, Words.T("spell.whose"),
                                          m =>
                                          {
                                              whose = m;
                                              search.Text = "";
                                              chosen = null;
                                              frozen = false;
                                              ShowSpells();
                                              Describe();
                                              Redraw();
                                              Hint();
                                          },
                                          280,
                                          m => _icons.Of(m.GfxId));

            search.TextChanged += (_, _) =>
            {
                if ((search.Text ?? "").Length > 0) whose = null;
                ShowSpells();
                Redraw();
            };

            showDead.IsCheckedChanged += (_, _) =>
            {
                bool on = showDead.IsChecked == true;
                showDead.Content = on ? Words.T("spell.showSpells") : Words.T("spell.showDead");

                deadList.IsVisible = on;
                spellList.IsVisible = !on;

                if (!on) { ShowSpells(); return; }

                var dead = _spells!.Dead();
                deadList.ItemsSource = dead;
                status.Text = Words.T("spell.deadList", dead.Count, dead.Sum(d => d.Levels));
            };

            spellList.SelectionChanged += (_, _) =>
            {
                chosen = spellList.SelectedItem as SpellRow;
                frozen = false;
                Describe();
                Redraw();
                Hint();
            };

            grid.HoveredChanged += cell =>
            {
                if (cell < 0) return;
                if (moveCaster.IsChecked == true) return;
                Aim(cell);
            };

            grid.Clicked += cell =>
            {
                if (moveCaster.IsChecked == true)
                {
                    caster = cell;
                    moveCaster.IsChecked = false;
                    frozen = false;
                    Redraw();
                    Hint();
                    return;
                }

                // Click freezes the aim, the way the reference tool does: sweep to explore, click
                // to hold it still while you read the numbers.
                frozen = !frozen;
                if (!frozen) Aim(grid.PointerCell());
                Hint();
            };

            moveCaster.IsCheckedChanged += (_, _) => Hint();

            var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            foreach (var control in new Control[] { monsterPicker, search, showDead, moveCaster, status })
            {
                control.Margin = new Thickness(0, 0, 12, 6);
                if (control is not TextBox) control.VerticalAlignment = VerticalAlignment.Center;
                top.Children.Add(control);
            }

            var lists = new Grid();
            lists.Children.Add(spellList);
            lists.Children.Add(deadList);

            var left = new Grid { RowDefinitions = new RowDefinitions("*,Auto,190") };
            Grid.SetRow(lists, 0);

            var effectsLabel = Skin.Heading(Words.T("spell.effects"));
            effectsLabel.Margin = new Thickness(0, 12, 0, 6);
            Grid.SetRow(effectsLabel, 1);
            Grid.SetRow(effectList, 2);

            left.Children.Add(lists);
            left.Children.Add(effectsLabel);
            left.Children.Add(effectList);

            var side = new StackPanel { Spacing = 10, Margin = new Thickness(12, 0, 0, 0) };
            side.Children.Add(Skin.Card(card, 10));
            side.Children.Add(hint);
            side.Children.Add(new ScrollViewer
            {
                Content = grid,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            });
            side.Children.Add(Legend());

            var split = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto") };
            Grid.SetColumn(left, 0);
            Grid.SetColumn(side, 2);
            split.Children.Add(left);
            split.Children.Add(side);

            var layout = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(top, Dock.Top);
            layout.Children.Add(top);
            layout.Children.Add(split);

            ShowSpells();
            Describe();
            Redraw();
            Hint();
            return layout;
        }

        private void Load()
        {
            _spells = new SpellCatalogue(_world.Text, _world.Complaints.Add);
            _monsters = new MonsterCatalogue(_world.Text, _world.Complaints.Add);

            _allSpells = _spells.Ready ? _spells.All() : new List<SpellSummary>();
            _allMonsters = _monsters.Ready ? _monsters.All() : new List<MonsterSummary>();
            _loaded = true;
        }

        private static readonly IBrush Reach = new SolidColorBrush(Color.FromArgb(0x44, 0x5F, 0xA8, 0xD3));
        private static readonly IBrush Hit = new SolidColorBrush(Color.FromArgb(0x99, 0xE0, 0x70, 0x5F));

        private static bool Reaches(SpellLevelInfo level, int from, int to)
        {
            int away = MapGeometry.Distance(from, to);
            if (away < level.MinRange || away > level.MaxRange) return false;
            return !level.CastInLine || InLine(from, to);
        }

        /// <summary>Whether two cells share a row or a column, which is what casting in line means.</summary>
        private static bool InLine(int from, int to)
        {
            var a = MapGeometry.CellToPoint(from);
            var b = MapGeometry.CellToPoint(to);
            return a.X == b.X || a.Y == b.Y;
        }

        private static string Short(string name) => name.Length <= 14 ? name : name[..13] + "…";

        private static Control Legend()
        {
            var row = new WrapPanel();
            row.Children.Add(Skin.Key(Reach, Words.T("spell.reach")));
            row.Children.Add(Skin.Key(Hit, Words.T("spell.wouldHit")));
            row.Children.Add(Skin.Key(Skin.AuthoredBrush, Words.T("spell.caster")));
            return row;
        }

        /// <summary>One dead effect, and how much of the game it is holding.</summary>
        private static Control DeadRow(SpellCatalogue.DeadEffect dead)
        {
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("56,*,150") };

            Cell(line, 0, dead.EffectId.ToString(), Skin.WrongBrush);

            var what = new TextBlock
            {
                Text = dead.Description.Length > 0 ? dead.Description : Words.T("spell.noWords"),
                Foreground = dead.Description.Length > 0 ? Skin.TextBrush : Skin.TextFaintBrush,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(what, 1);
            line.Children.Add(what);

            Cell(line, 2, Words.T("spell.deadRow", dead.Levels, dead.Spells), Skin.TextSoftBrush);
            return line;
        }

        private static Control SpellLine(SpellRow row)
        {
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("62,*,52,74") };

            Cell(line, 0, row.SpellId.ToString(), Skin.TextFaintBrush);

            var name = new TextBlock
            {
                Text = row.Name,
                Foreground = Skin.TextBrush,
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(name, 1);
            line.Children.Add(name);

            Cell(line, 2, row.Level == null ? "—" : row.Level.ApCost + " PA", Skin.MeasuredBrush);

            // The 0-0 range is on the row and not buried in the card, because it is the difference
            // between a spell that works and one that has never been cast.
            Cell(line, 3,
                 row.Level == null ? "" : (row.Level.OnSelfOnly ? Words.T("spell.selfOnly")
                                                                : $"{row.Level.MinRange}–{row.Level.MaxRange}"),
                 row.Level is { OnSelfOnly: true } ? Skin.WrongBrush : Skin.TextFaintBrush);

            return line;
        }

        private static Control EffectLine(SpellEffectInfo effect)
        {
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("48,*,84,64,104") };

            Cell(line, 0, effect.EffectId.ToString(),
                 effect.Critical ? Skin.AuthoredBrush : Skin.TextFaintBrush);

            var what = new TextBlock
            {
                Text = effect.Description.Length > 0 ? effect.Description : Words.T("spell.noWords"),
                Foreground = effect.Description.Length > 0 ? Skin.TextBrush : Skin.TextFaintBrush,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(what, 1);
            line.Children.Add(what);

            Cell(line, 2, effect.Roll, Skin.TextBrush);
            Cell(line, 3, Shape(effect), Skin.MeasuredBrush);

            // The column that says whether the spell works. Effect 108 is why it is here: the card
            // says the spell heals, the animation plays, and nobody's life goes up.
            Cell(line, 4, effect.Support switch
            {
                EffectSupportKind.Direct => Words.T("spell.applied"),
                EffectSupportKind.Characteristic => Words.T("spell.asCharac"),
                _ => Words.T("spell.panelOnly"),
            }, effect.Support switch
            {
                EffectSupportKind.Direct => Skin.DoneBrush,
                EffectSupportKind.Characteristic => Skin.MeasuredBrush,
                _ => Skin.WrongBrush,
            });

            return line;
        }

        /// <summary>
        /// The area as a letter and a size, and the letter raw when this build does not know it.
        /// </summary>
        /// <remarks>
        /// Showing the unknown ones is the point: a shape <see cref="Zone"/> does not handle falls
        /// back to the centre cell alone, so the spell quietly hits one square instead of nine and
        /// nothing anywhere says so.
        /// </remarks>
        private static string Shape(SpellEffectInfo effect)
        {
            if (effect.ZoneShape == 0) return "";

            char letter = (char)effect.ZoneShape;
            bool known = effect.ZoneShape is Zone.Punto or Zone.Circulo or Zone.Aspa or Zone.Cruz
                                          or Zone.TodoElMapa or Zone.Linea or Zone.MediaLinea
                                          or Zone.Rombo or Zone.CruzCompleta or Zone.Cuadrado;

            return known ? $"{letter}{effect.ZoneSize}" : $"{letter}? {effect.ZoneSize}";
        }

        private static void Cell(Grid line, int column, string text, IBrush colour)
        {
            var block = Skin.Fixed(text, colour);
            block.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(block, column);
            line.Children.Add(block);
        }
    }
}
