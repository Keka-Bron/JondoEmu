using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Jondo.Unity.Launcher.UI.Widgets
{
    /// <summary>
    /// The little flag of the language buttons, and of the game languages' dropdown.
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
                case "en": ReinoUnido(context, r); break;
                case "de": Alemania(context, r); break;
                case "pt": Brasil(context, r); break;
                // A pack the client brings some day and this does not know: a blank flag rather
                // than another country's, which would be telling a lie.
                default: context.FillRectangle(new SolidColorBrush(Color.FromRgb(96, 84, 70)), r); break;
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

        /// <summary>Black, red and gold, in horizontal thirds.</summary>
        private static void Alemania(DrawingContext context, Rect r)
        {
            double tercio = r.Height / 3;
            context.FillRectangle(Brushes.Black, new Rect(0, 0, r.Width, tercio));
            context.FillRectangle(new SolidColorBrush(Color.FromRgb(221, 0, 0)), new Rect(0, tercio, r.Width, tercio));
            context.FillRectangle(new SolidColorBrush(Color.FromRgb(255, 206, 0)), new Rect(0, tercio * 2, r.Width, tercio));
        }

        /// <summary>
        /// Brazil for Portuguese: the green field, the yellow rhombus and the blue globe with its band.
        /// </summary>
        /// <remarks>
        /// The pt pack is shown with Brazil's flag rather than Portugal's.
        /// The globe's stars and motto do not fit in fourteen pixels: what is left is the white
        /// band, an arc of a circle whose centre lies below the globe, clipped to it.
        /// </remarks>
        private static void Brasil(DrawingContext context, Rect r)
        {
            context.FillRectangle(new SolidColorBrush(Color.FromRgb(0, 155, 58)), r);

            // The rhombus stops 1.7 units short of each edge on a 20 by 14 flag.
            double margenX = r.Width * 1.7 / 20, margenY = r.Height * 1.7 / 14;
            var rombo = new StreamGeometry();
            using (var g = rombo.Open())
            {
                g.BeginFigure(new Point(r.Width / 2, margenY), true);
                g.LineTo(new Point(r.Width - margenX, r.Height / 2));
                g.LineTo(new Point(r.Width / 2, r.Height - margenY));
                g.LineTo(new Point(margenX, r.Height / 2));
                g.EndFigure(true);
            }
            context.DrawGeometry(new SolidColorBrush(Color.FromRgb(254, 223, 0)), null, rombo);

            var centro = new Point(r.Width / 2, r.Height / 2);
            double radio = r.Height * 0.25;
            context.DrawEllipse(new SolidColorBrush(Color.FromRgb(0, 39, 118)), null, centro, radio, radio);

            var arco = new Point(centro.X - radio * 0.5, centro.Y + radio * 2.6);
            double radioArco = radio * 2.65;
            using (context.PushGeometryClip(new EllipseGeometry(new Rect(centro.X - radio, centro.Y - radio, radio * 2, radio * 2))))
            {
                context.DrawEllipse(null, new Pen(Brushes.White, radio * 0.3), arco, radioArco, radioArco);
            }
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
