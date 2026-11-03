using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Jondo.Unity.Launcher;
using Jondo.Unity.Launcher.UI;
using ButtonControl = Avalonia.Controls.Button;

namespace Jondo.Unity.Deobfuscator.UI;

internal static class DesktopTheme
{
    internal static SolidColorBrush Brush(uint color) => new(Color.FromUInt32(color));
    internal static readonly IBrush Gold = Brush(LauncherPalette.SoftGold);
    internal static readonly IBrush Muted = Brush(LauncherPalette.MutedGold);
    internal static readonly IBrush Green = Brush(LauncherPalette.OnlineGreen);
    internal static readonly IBrush Red = Brush(LauncherPalette.Red);
    internal static readonly FontFamily Mono = new("Cascadia Mono, DejaVu Sans Mono, Consolas, monospace");
    private static readonly FontFamily Title = new("Cinzel, Trebuchet MS, DejaVu Serif, serif");

    internal static void Apply(Window window)
    {
        window.Background = Brush(LauncherPalette.Background);
        window.Foreground = Brush(LauncherPalette.BaseText);
        window.FontFamily = new FontFamily("Inter, DejaVu Sans, Segoe UI, sans-serif");
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.Styles.Add(new Style(s => s.OfType<Button>().Class("workshop"))
        {
            Setters =
            {
                new Setter(ButtonControl.BackgroundProperty, Gradient(0xC822150C, 0xC8160D08)),
                new Setter(ButtonControl.ForegroundProperty, Gold),
                new Setter(ButtonControl.BorderBrushProperty, Brush(LauncherPalette.BorderBrown)),
                new Setter(ButtonControl.BorderThicknessProperty, new Thickness(1)),
                new Setter(ButtonControl.CornerRadiusProperty, new CornerRadius(5)),
                new Setter(ButtonControl.FontWeightProperty, FontWeight.Bold),
            },
        });
        window.Styles.Add(new Style(s => s.OfType<Button>().Class("primary"))
        {
            Setters =
            {
                new Setter(ButtonControl.BackgroundProperty, Gradient(LauncherPalette.GreenTop, LauncherPalette.GreenBottom)),
                new Setter(ButtonControl.ForegroundProperty, Brushes.White),
                new Setter(ButtonControl.BorderBrushProperty, Brush(LauncherPalette.GreenBorder)),
            },
        });
        window.Styles.Add(new Style(s => s.OfType<Button>().Class("selected"))
        {
            Setters =
            {
                new Setter(ButtonControl.BackgroundProperty, Brush(LauncherPalette.LightBrown)),
                new Setter(ButtonControl.BorderBrushProperty, Brush(LauncherPalette.LightGold)),
            },
        });
        window.Styles.Add(new Style(s => s.OfType<Button>().Class("workshop").Class(":pointerover")
            .Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters =
            {
                new Setter(ContentPresenter.BackgroundProperty, Gradient(LauncherPalette.LightBrown, 0xFF824B23)),
                new Setter(ContentPresenter.BorderBrushProperty, Brush(LauncherPalette.GoldBorder)),
                new Setter(ContentPresenter.ForegroundProperty, Brushes.White),
            },
        });
        window.Styles.Add(new Style(s => s.OfType<Button>().Class("primary").Class(":pointerover")
            .Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty,
                Gradient(LauncherPalette.GreenTopHover, LauncherPalette.GreenBottomHover)) },
        });
        window.Styles.Add(new Style(s => s.OfType<TextBox>())
        {
            Setters =
            {
                new Setter(TextBox.BackgroundProperty, Brush(LauncherPalette.FieldBackground)),
                new Setter(TextBox.ForegroundProperty, Brush(LauncherPalette.FieldText)),
                new Setter(TextBox.BorderBrushProperty, Brush(LauncherPalette.BorderBrown)),
                new Setter(TextBox.CornerRadiusProperty, new CornerRadius(5)),
            },
        });
    }

    internal static TextBlock Caption(string text) => new()
    {
        Text = text, Foreground = Muted, Margin = new Thickness(0, 0, 0, 6),
        TextWrapping = TextWrapping.Wrap,
    };

    internal static Button Button(string text, bool primary = false)
    {
        var button = new Button { Content = text, Padding = new Thickness(16, 8), MinHeight = 36 };
        button.Classes.Add("workshop");
        if (primary) button.Classes.Add("primary");
        return button;
    }

    internal static Border Card(Control content, uint fill = LauncherPalette.CardFill) => new()
    {
        Child = content, Background = Brush(fill), BorderBrush = Brush(LauncherPalette.BorderBrown),
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(16),
    };

    internal static Control Backdrop(Window window, Control content)
    {
        var layers = new Panel();
        foreach (string name in new[] { "servidor_fondo.jpg", "bg.jpg" })
        {
            try
            {
                string path = Path.Combine(Paths.Root, "launcher_assets", name);
                if (!File.Exists(path)) continue;
                var photo = new Bitmap(path);
                layers.Children.Add(new Image { Source = photo, Stretch = Stretch.UniformToFill,
                    IsHitTestVisible = false });
                window.Closed += (_, _) => photo.Dispose();
                break;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { /* Optional artwork: the palette remains readable without it. */ }
        }
        layers.Children.Add(new Border { Background = Brush(0x70080402), IsHitTestVisible = false });
        layers.Children.Add(content);
        return layers;
    }

    private static LinearGradientBrush Gradient(uint top, uint bottom) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromUInt32(top), 0), new GradientStop(Color.FromUInt32(bottom), 1) },
    };

    /// <summary>The original two-line, outlined gold sign, drawn at the current display scale.</summary>
    internal sealed class Logo : Control
    {
        public Logo() { Height = 84; IsHitTestVisible = false; }

        public override void Render(DrawingContext context)
        {
            if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
            var face = new Typeface(Title, FontStyle.Normal, FontWeight.Bold);
            double size = Math.Min(Bounds.Height * 0.52, Bounds.Width / 8);
            foreach (var (word, scale, centerY) in new[] { ("JONDO", 1.0, 0.36), ("DEOBFUSCATOR", 0.46, 0.79) })
            {
                var text = new FormattedText(word, System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, face, size * scale, Brushes.White);
                var geometry = text.BuildGeometry(new Point(-text.Width / 2, -text.Height / 2));
                if (geometry == null) continue;
                using (context.PushTransform(Matrix.CreateRotation(-2.45 * Math.PI / 180) *
                    Matrix.CreateTranslation(Bounds.Width / 2, Bounds.Height * centerY)))
                {
                    using (context.PushTransform(Matrix.CreateTranslation(0, 3)))
                        context.DrawGeometry(Brush(0x78000000), null, geometry);
                    context.DrawGeometry(null, new Pen(Brush(0xEB261406), Math.Max(5, size * 0.17))
                        { LineJoin = PenLineJoin.Round }, geometry);
                    context.DrawGeometry(Gradient(0xFFFFF5C8, 0xFFA86410),
                        new Pen(Brush(LauncherPalette.BorderBrown), 2) { LineJoin = PenLineJoin.Round }, geometry);
                }
            }
        }
    }
}
