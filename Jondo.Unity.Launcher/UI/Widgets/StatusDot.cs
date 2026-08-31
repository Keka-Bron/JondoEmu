using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Jondo.Unity.Launcher.UI.Widgets
{
    /// <summary>
    /// El punto con halo del estado del servidor.
    /// </summary>
    /// <remarks>
    /// Verde cuando contesta y rojo cuando no, con un halo del mismo color al 27 % por detras. Es
    /// el bloque <c>.server-status</c> de la web; el rotulo de al lado ya no se pinta aqui, lo pone
    /// un TextBlock normal, porque Avalonia si sabe alinear texto sin ayuda.
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
