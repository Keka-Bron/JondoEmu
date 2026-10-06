using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Jondo.Unity.Launcher.UI.Widgets
{
    /// <summary>
    /// The server status dot with a halo.
    /// </summary>
    /// <remarks>
    /// Green when it answers and red when it does not, with a halo of the same colour at 27 % behind. It is
    /// the website's <c>.server-status</c> block; the label next to it is no longer painted here, a
    /// normal TextBlock puts it, because Avalonia does know how to align text without help.
    /// </remarks>
    internal sealed class StatusDot : Control
    {
        public static readonly StyledProperty<bool> OnlineProperty =
            AvaloniaProperty.Register<StatusDot, bool>(nameof(Online));

        static StatusDot() => AffectsRender<StatusDot>(OnlineProperty);

        public StatusDot()
        {
            Width = 16;
            Height = 16;
        }

        public bool Online
        {
            get => GetValue(OnlineProperty);
            set => SetValue(OnlineProperty, value);
        }

        public override void Render(DrawingContext context)
        {
            Color color = Online ? LauncherSkin.DotGreen : LauncherSkin.Red;
            var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
            double radius = System.Math.Max(3.5, Bounds.Height / 4);

            context.DrawEllipse(new SolidColorBrush(color, 0.27), null, centre, radius + 3, radius + 3);
            context.DrawEllipse(new SolidColorBrush(color), null, centre, radius, radius);
        }
    }
}
