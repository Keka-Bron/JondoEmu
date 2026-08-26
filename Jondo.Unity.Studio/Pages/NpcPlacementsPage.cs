using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Jondo.Unity.Studio.Data;
using Jondo.Unity.World.Content;

namespace Jondo.Unity.Studio.Pages
{
    /// <summary>
    /// Every NPC placement, and — the column that matters — where each one came from.
    /// </summary>
    /// <remarks>
    /// The provenance column is not decoration. Six months from now nobody will remember whether a
    /// cell number was measured off a capture or typed in by hand, and without it on screen the two
    /// become indistinguishable. It is the single thing that stops the authored layer rotting into
    /// folklore.
    /// </remarks>
    public sealed class NpcPlacementsPage : IStudioPage
    {
        private readonly WorldData _world;

        public NpcPlacementsPage(WorldData world) => _world = world;

        public string Title => "NPC placements";

        public override string ToString() => Title;

        private sealed record Row(long Map, int Npc, int Cell, int Facing, string From);

        public Control Build()
        {
            var all = _world.NpcPlacements.Rows
                .Select(pair => new Row(pair.Value.Value.MapId,
                                        pair.Value.Value.NpcId,
                                        pair.Value.Value.Cell,
                                        pair.Value.Value.Orientation,
                                        pair.Value.From.ToString()))
                .OrderBy(row => row.Map).ThenBy(row => row.Cell)
                .ToList();

            var list = new ListBox
            {
                ItemTemplate = new FuncDataTemplate<Row>((row, _) => Line(row), supportsRecycling: true),
                ItemsSource = all,
            };

            var search = new TextBox
            {
                Watermark = "map id, or npc id",
                Width = 260,
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            var counted = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x9A)),
                VerticalAlignment = VerticalAlignment.Center,
            };

            void Refilter()
            {
                string needle = (search.Text ?? "").Trim();
                var shown = needle.Length == 0
                    ? all
                    : all.Where(row => row.Map.ToString().Contains(needle) ||
                                       row.Npc.ToString().Contains(needle)).ToList();

                list.ItemsSource = shown;
                counted.Text = shown.Count == all.Count
                    ? $"{all.Count:N0} placements"
                    : $"{shown.Count:N0} of {all.Count:N0} placements";
            }

            search.TextChanged += (_, _) => Refilter();
            Refilter();

            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 14,
                Children = { search, counted },
            };

            var layout = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(header, Dock.Top);
            header.Margin = new Avalonia.Thickness(0, 0, 0, 12);
            layout.Children.Add(header);
            layout.Children.Add(list);
            return layout;
        }

        private static Control Line(Row row)
        {
            var line = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("120,90,70,70,*"),
            };

            Add(line, 0, row.Map.ToString());
            Add(line, 1, row.Npc.ToString());
            Add(line, 2, row.Cell.ToString());
            Add(line, 3, row.Facing.ToString());
            Add(line, 4, row.From, dim: true);
            return line;
        }

        private static void Add(Grid line, int column, string text, bool dim = false)
        {
            var block = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                Foreground = new SolidColorBrush(dim ? Color.FromRgb(0x8A, 0x8F, 0x9A)
                                                    : Color.FromRgb(0xC8, 0xCD, 0xD6)),
            };
            Grid.SetColumn(block, column);
            line.Children.Add(block);
        }
    }
}
