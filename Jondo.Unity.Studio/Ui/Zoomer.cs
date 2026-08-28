using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Jondo.Unity.Studio.Controls;

namespace Jondo.Unity.Studio.Ui
{
    /// <summary>
    /// Two buttons and a number, for making the map bigger.
    /// </summary>
    /// <remarks>
    /// Here rather than repeated on three screens, and it exists at all because the map was the
    /// smallest thing on a screen whose whole point is the map: 600 pixels of grid next to 1,300
    /// pixels of table. The table is how you find a thing; the map is the thing.
    /// </remarks>
    public static class Zoomer
    {
        private const double Step = 0.25;

        public static Control For(CellGrid grid, double start = 1.5)
        {
            grid.Zoom = start;

            var reading = new TextBlock
            {
                Foreground = Skin.TextSoftBrush,
                FontFamily = Skin.Mono,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 42,
                TextAlignment = Avalonia.Media.TextAlignment.Center,
            };

            var smaller = new Button { Content = "−", Padding = new Thickness(11, 4), FontSize = 15 };
            var bigger = new Button { Content = "+", Padding = new Thickness(11, 4), FontSize = 15 };

            void Show() => reading.Text = $"{grid.Zoom * 100:0}%";

            smaller.Click += (_, _) => { grid.Zoom -= Step; Show(); };
            bigger.Click += (_, _) => { grid.Zoom += Step; Show(); };

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
            };

            row.Children.Add(smaller);
            row.Children.Add(reading);
            row.Children.Add(bigger);

            Show();
            return row;
        }
    }
}
