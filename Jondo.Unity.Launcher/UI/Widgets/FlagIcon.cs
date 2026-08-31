using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Jondo.Unity.Launcher.UI.Widgets
{
    /// <summary>
    /// La banderita de los botones de idioma.
    /// </summary>
    /// <remarks>
    /// España y Francia son tres franjas y no tienen misterio. La del Reino Unido sí: es la Union
    /// Jack y lleva DOS aspas además de la cruz. La primera versión la dibujaba con una cruz blanca
    /// y otra roja sobre azul y nada más, o sea la bandera de otro país; se notaba a simple vista.
    ///
    /// El orden de las capas es el de la bandera de verdad y hay que respetarlo, porque cada una
    /// tapa parte de la anterior:
    ///
    ///   1. el campo azul
    ///   2. el aspa blanca  (San Andrés, Escocia)
    ///   3. el aspa roja    (San Patricio, Irlanda) -- más fina, va encima de la blanca
    ///   4. la cruz blanca  (el borde de la de San Jorge)
    ///   5. la cruz roja    (San Jorge, Inglaterra)
    ///
    /// A veinte por catorce píxeles no cabe el contracambiado de las diagonales -- el desplazamiento
    /// que hace que el aspa roja no esté centrada en la blanca -- y no se dibuja: a este tamaño no
    /// se distinguiría y complicaría el trazado para nada.
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

        /// <summary>Rojo, amarillo el doble de ancho, y rojo.</summary>
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

            // Las dos aspas. El grosor sale de la altura para que la bandera aguante si algún día
            // se dibuja más grande.
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

            // Y la cruz encima, con su borde blanco.
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
