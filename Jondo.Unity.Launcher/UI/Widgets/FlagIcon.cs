using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Jondo.Unity.Launcher.UI.Widgets
{
    /// <summary>
    /// The little flag of the language buttons.
    /// </summary>
    /// <remarks>
    /// Spain and France are three stripes and have no mystery. The United Kingdom's does: it is the Union
    /// Jack and carries TWO saltires on top of the cross. The first version drew it with a white cross
    /// and a red one on blue and nothing else, that is another country's flag; it showed at a glance.
    ///
    /// The order of the layers is that of the real flag and it has to be respected, because each one
    /// covers part of the previous one:
    ///
    ///   1. the blue field
    ///   2. the white saltire  (Saint Andrew, Scotland)
    ///   3. the red saltire    (Saint Patrick, Ireland) -- thinner, it goes on top of the white
    ///   4. the white cross    (the edge of Saint George's)
    ///   5. the red cross      (Saint George, England)
    ///
    /// At twenty by fourteen pixels the counterchange of the diagonals does not fit -- the offset
    /// that makes the red saltire not centred on the white one -- and it is not drawn: at this size it
    /// would not be distinguishable and it would complicate the drawing for nothing.
    /// </remarks>
    internal sealed class FlagIcon : Control
    {
        public static readonly StyledProperty<string> CodeProperty =
            AvaloniaProperty.Register<FlagIcon, string>(nameof(Code), "es");

        static FlagIcon() => AffectsRender<FlagIcon>(CodeProperty);

        public FlagIcon()
        {
            Width = 21;
            Height = 14;
        }

        public string Code
        {
            get => GetValue(CodeProperty);
            set => SetValue(CodeProperty, value);
        }

        public override void Render(DrawingContext context)
        {
            var r = new Rect(Bounds.Size);

            switch (Code)
            {
                case "es": Espana(context, r); break;
                case "fr": Francia(context, r); break;
                default: ReinoUnido(context, r); break;
            }

            context.DrawRectangle(new Pen(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0))), r);
        }

        /// <summary>Red, yellow twice as wide, and red.</summary>
        private static void Espana(DrawingContext context, Rect r)
        {
            context.FillRectangle(new SolidColorBrush(Color.FromRgb(198, 11, 30)), r);
            context.FillRectangle(new SolidColorBrush(Color.FromRgb(255, 196, 0)),
                new Rect(0, r.Height * 0.25, r.Width, r.Height * 0.5));
        }

        /// <summary>Azul, blanco y rojo, en vertical.</summary>
        private static void Francia(DrawingContext context, Rect r)
        {
            double tercio = r.Width / 3;
            context.FillRectangle(new SolidColorBrush(Color.FromRgb(0, 85, 164)), new Rect(0, 0, tercio, r.Height));
            context.FillRectangle(Brushes.White, new Rect(tercio, 0, tercio, r.Height));
            context.FillRectangle(new SolidColorBrush(Color.FromRgb(239, 65, 53)), new Rect(tercio * 2, 0, tercio, r.Height));
        }

        private static void ReinoUnido(DrawingContext context, Rect r)
        {
            var azul = new SolidColorBrush(Color.FromRgb(1, 33, 105));
            var rojo = new SolidColorBrush(Color.FromRgb(200, 16, 46));

            context.FillRectangle(azul, r);

            // The two saltires. The thickness comes from the height so that the flag holds up if some day
            // it is drawn bigger.
            var aspaBlanca = new Pen(Brushes.White, r.Height * 0.30);
            var aspaRoja = new Pen(rojo, r.Height * 0.14);

            using (context.PushClip(r))
            {
                foreach (var lapiz in new[] { aspaBlanca, aspaRoja })
                {
                    context.DrawLine(lapiz, new Point(0, 0), new Point(r.Width, r.Height));
                    context.DrawLine(lapiz, new Point(r.Width, 0), new Point(0, r.Height));
                }
            }

            // And the cross on top, with its white edge.
            var cruzBlanca = new Pen(Brushes.White, r.Height * 0.42);
            var cruzRoja = new Pen(rojo, r.Height * 0.24);

            foreach (var lapiz in new[] { cruzBlanca, cruzRoja })
            {
                context.DrawLine(lapiz, new Point(0, r.Height / 2), new Point(r.Width, r.Height / 2));
                context.DrawLine(lapiz, new Point(r.Width / 2, 0), new Point(r.Width / 2, r.Height));
            }
        }
    }
}
