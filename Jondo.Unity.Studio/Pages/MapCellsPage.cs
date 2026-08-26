using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Jondo.Unity.Studio.Controls;
using Jondo.Unity.Studio.Data;
using Jondo.Unity.World.Maps;

namespace Jondo.Unity.Studio.Pages
{
    /// <summary>
    /// One map's cells, painted, with its four neighbours to walk the world by.
    /// </summary>
    /// <remarks>
    /// Read only in phase one: it shows what the data says and changes nothing. Painting comes in
    /// phase five, once there is content worth putting on top of a map.
    /// </remarks>
    public sealed class MapCellsPage : IStudioPage
    {
        private readonly WorldData _world;

        public MapCellsPage(WorldData world) => _world = world;

        public string Title => "Map cells";

        public override string ToString() => Title;

        public Control Build()
        {
            var grid = new CellGrid();
            var caption = Dim("");
            var summary = Dim("");

            var chooser = new TextBox
            {
                Watermark = "map id",
                Width = 200,
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            void Show(long mapId)
            {
                _world.Maps.TryGetValue(mapId, out var cells);
                grid.Show(cells);

                summary.Text = cells == null
                    ? $"map {mapId} has no cell data"
                    : $"map {mapId} · {cells.Walkable.Count} walkable · " +
                      $"{cells.WalkableInFight.Count} walkable in a fight · " +
                      $"{cells.SightBlockers.Count} block sight";
            }

            chooser.TextChanged += (_, _) =>
            {
                if (long.TryParse((chooser.Text ?? "").Trim(), out long mapId)) Show(mapId);
            };

            grid.HoveredChanged += cell =>
            {
                caption.Text = cell < 0
                    ? ""
                    : $"cell {cell} · row {cell / MapGeometry.MapWidth}, column {cell % MapGeometry.MapWidth}";
            };

            // Something on screen from the first moment rather than an empty frame and a prompt.
            long first = _world.Maps.Keys.OrderBy(id => id).FirstOrDefault();
            if (first != 0)
            {
                chooser.Text = first.ToString();
                Show(first);
            }

            var legend = new StackPanel { Spacing = 6, Margin = new Avalonia.Thickness(24, 0, 0, 0) };
            legend.Children.Add(OverviewPage.Heading("Legend"));
            legend.Children.Add(Swatch(Color.FromRgb(0x4C, 0x7A, 0x5A), "walkable"));
            legend.Children.Add(Swatch(Color.FromRgb(0x8A, 0x5A, 0x2E), "walkable, but not during a fight"));
            legend.Children.Add(Swatch(Color.FromRgb(0x36, 0x4B, 0x73), "seen through, never walked"));
            legend.Children.Add(Swatch(Color.FromRgb(0x2B, 0x2E, 0x35), "solid — nothing passes"));
            legend.Children.Add(caption);

            var side = new StackPanel { Spacing = 12, Children = { chooser, summary } };

            var body = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20 };
            body.Children.Add(new ScrollViewer { Content = grid, MaxHeight = 620 });
            body.Children.Add(legend);

            var layout = new StackPanel { Spacing = 14 };
            layout.Children.Add(side);
            layout.Children.Add(body);
            return new ScrollViewer { Content = layout };
        }

        private static TextBlock Dim(string text) => new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x9A)),
        };

        private static Control Swatch(Color colour, string what)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new Border
            {
                Width = 16,
                Height = 16,
                Background = new SolidColorBrush(colour),
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = what,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xCD, 0xD6)),
            });
            return row;
        }
    }
}
