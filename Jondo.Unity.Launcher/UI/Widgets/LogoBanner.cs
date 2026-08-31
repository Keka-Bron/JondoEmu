using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Jondo.Unity.Launcher.UI.Widgets
{
    /// <summary>
    /// El rótulo de «JONDO EMU», encendido como un tubo de neón.
    /// </summary>
    /// <remarks>
    /// Está dibujado, no es una imagen: el trazo del logotipo de Ankama no se puede reletrar, así
    /// que lo que se hace es un rótulo propio con el mismo aire —oro con degradado, contorno oscuro
    /// grueso y un poco de arco— usando la tipografía del lanzador. El contorno sale de la
    /// geometría del texto: <c>BuildGeometry</c> da la silueta de las letras, y se rellena y se
    /// traza en la misma pasada.
    ///
    /// <b>Ojo con la brocha.</b> <c>FormattedText.BuildGeometry</c> devuelve la silueta VACÍA si el
    /// texto no lleva brocha puesta. Con <c>null</c> no lanza, no avisa y no pinta: el rótulo estuvo
    /// invisible desde que el lanzador pasó a Avalonia y nadie se enteró.
    /// </remarks>
    internal sealed class LogoBanner : Control
    {
        /// <summary>Las dos palabras. El lanzador pone JONDO EMU y el servidor JONDO SERVER.</summary>
        public string First { get; init; } = "JONDO";
        public string Second { get; init; } = "EMU";

        /// <summary>Cuánto se arquea el rótulo, en grados de giro de la primera y la última letra.</summary>
        private const double Arc = 7;

        /// <summary>Si al aparecer hace el arranque del tubo. Falso: sale ya encendido.</summary>
        /// <remarks>
        /// Existe para poder fotografiarlo encendido sin esperar segundo y pico, que es lo que se
        /// hace al trabajar el diseño. En la ventana no se toca.
        /// </remarks>
        public bool ConArranque { get; init; } = true;

        // ═══════════════════════════════════════════════════════════════════
        //  El neón
        // ═══════════════════════════════════════════════════════════════════
        //
        // Un tubo de neón no se enciende: ARRANCA. Da un fogonazo, se apaga, tartamudea unas
        // cuantas veces cada vez más seguidas y acaba quedándose. Ya encendido no está quieto del
        // todo: tiembla un poco y de tarde en tarde se va un instante.
        //
        // El arranque va escrito a mano en una tabla y no sale de ningún azar, porque es una
        // COREOGRAFÍA: los tiempos y el orden de los fogonazos son lo que lo hace parecer un tubo
        // de verdad, y dejarlos al azar lo convierte en una luz estropeada. Lo que sí es al azar es
        // el parpadeo de después, que tiene que ser impredecible.

        /// <summary>El arranque: hasta qué segundo dura cada tramo, y con cuánta luz.</summary>
        private static readonly (double Hasta, double Luz)[] Arranque =
        {
            (0.12, 0.00),   // un instante a oscuras antes de nada
            (0.20, 1.00),   // el primer fogonazo
            (0.32, 0.04),
            (0.38, 0.85),
            (0.46, 0.02),
            (0.58, 0.00),   // parece que no arranca
            (0.66, 1.00),
            (0.72, 0.08),
            (0.80, 0.95),
            (0.86, 0.14),
            (0.94, 1.00),
            (1.00, 0.32),   // el último tartamudeo, ya flojo
            (1.20, 1.00),   // y se queda
        };

        /// <summary>Cuántas capas de halo. Seis es donde deja de notarse añadir más.</summary>
        private const int Capas = 6;

        private readonly DispatcherTimer _reloj = new() { Interval = TimeSpan.FromMilliseconds(33) };
        private readonly Random _azar = new Random(20260830);
        private double _tiempo;
        private double _brillo;
        private double _apagonHasta = -1;

        public LogoBanner()
        {
            IsHitTestVisible = false;
            _reloj.Tick += (_, _) => Latir();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            // El arranque empieza cada vez que el rótulo entra en pantalla, no una vez por proceso.
            _tiempo = ConArranque ? 0 : Arranque[Arranque.Length - 1].Hasta;
            _brillo = ConArranque ? 0 : 1;
            _apagonHasta = -1;
            _reloj.Start();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _reloj.Stop();
            base.OnDetachedFromVisualTree(e);
        }

        private void Latir()
        {
            _tiempo += 0.033;
            _brillo = _tiempo < Arranque[Arranque.Length - 1].Hasta ? LuzDelArranque() : LuzYaEncendido();
            InvalidateVisual();
        }

        private double LuzDelArranque()
        {
            foreach (var (hasta, luz) in Arranque)
            {
                if (_tiempo < hasta) return luz;
            }
            return 1;
        }

        private double LuzYaEncendido()
        {
            // El parpadeo de régimen: raro, corto, y nunca a oscuras del todo. Un tubo que se
            // apagara entero cada poco estaría estropeado, no encendido.
            if (_tiempo > _apagonHasta && _azar.NextDouble() < 0.004)
            {
                _apagonHasta = _tiempo + 0.05 + _azar.NextDouble() * 0.10;
            }
            if (_tiempo < _apagonHasta) return 0.30;

            // Y el temblor: dos senos de periodos que no encajan. Uno solo se ve mecánico a los
            // tres segundos de mirarlo.
            double lento = (1 + Math.Sin(_tiempo * 1.9)) / 2;
            double rapido = (1 + Math.Sin(_tiempo * 13.7 + 1.1)) / 2;
            return 0.86 + 0.10 * lento + 0.04 * rapido;
        }

        public override void Render(DrawingContext context)
        {
            if (Bounds.Height < 24 || Bounds.Width < 60) return;

            string text = (First + " " + Second).Trim();
            double size = Math.Min(Bounds.Height * 0.62, Bounds.Width / (text.Length * 0.62));
            if (size < 10) return;

            var face = new Typeface(LauncherSkin.Title, FontStyle.Normal, FontWeight.Bold);

            // El relleno sube de oro apagado a casi blanco con la luz; apagado del todo se queda en
            // el marrón del contorno, que es un tubo sin gas.
            var relleno = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Mezclar(LauncherSkin.BorderBrown, Color.FromRgb(255, 246, 206), _brillo), 0),
                    new GradientStop(Mezclar(LauncherSkin.BorderBrown, LauncherSkin.LightGold, _brillo), 0.5),
                    new GradientStop(Mezclar(LauncherSkin.BorderBrown, LauncherSkin.Gold, _brillo), 1),
                },
            };
            // Fino a proposito: es el filo oscuro que separa la letra del halo, no un borde. Grueso
            // se comia el oro de dentro y el rotulo se leia como un contorno hueco.
            var contorno = new Pen(new SolidColorBrush(Color.FromRgb(38, 22, 10)), Math.Max(1.5, size / 18))
            {
                LineJoin = PenLineJoin.Round,
            };
            var sombra = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));

            var anchos = new double[text.Length];
            double total = 0;
            for (int i = 0; i < text.Length; i++)
            {
                anchos[i] = Medir(text[i], face, size).WidthIncludingTrailingWhitespace;
                total += anchos[i];
            }

            double x = (Bounds.Width - total) / 2;
            double baseY = Bounds.Height / 2;

            for (int i = 0; i < text.Length; i++)
            {
                double t = text.Length <= 1 ? 0.5 : (double)i / (text.Length - 1);
                double angulo = (t - 0.5) * 2 * Arc;
                double subida = Math.Abs(t - 0.5) * size * 0.16;

                var glifo = Medir(text[i], face, size);
                var geometria = glifo.BuildGeometry(new Point(0, 0));
                if (geometria != null)
                {
                    var centro = new Point(x + anchos[i] / 2, baseY);
                    using (context.PushTransform(
                               Matrix.CreateTranslation(-anchos[i] / 2, -glifo.Height / 2) *
                               Matrix.CreateRotation(angulo * Math.PI / 180) *
                               Matrix.CreateTranslation(centro.X, centro.Y + subida)))
                    {
                        using (context.PushTransform(Matrix.CreateTranslation(0, size * 0.07)))
                        {
                            context.DrawGeometry(sombra, null, geometria);
                        }

                        // El resplandor: la misma silueta trazada seis veces, cada una más gorda y
                        // más transparente. Es un halo de pobre, y a este tamaño no se distingue de
                        // uno de verdad, que costaría un desenfoque por fotograma.
                        for (int capa = Capas; capa >= 1; capa--)
                        {
                            // Cae con el CUADRADO de la capa: así la de fuera es un velo y la de
                            // dentro es el filo. Cayendo lineal, las de fuera pesaban tanto como
                            // las de dentro y el halo se comía las letras.
                            double alfa = _brillo * 0.26 / (capa * capa);
                            if (alfa < 0.004) continue;

                            var color = capa > Capas / 2 ? LauncherSkin.Gold : LauncherSkin.LightGold;
                            var halo = new Pen(new SolidColorBrush(color, alfa),
                                               contorno.Thickness + capa * size * 0.11)
                            {
                                // REDONDO, las dos cosas. Por omisión un trazo grueso une en pico,
                                // y en las esquinas de una letra eso son pinchos: el resplandor
                                // salía como una estrella de picos en vez de un halo.
                                LineJoin = PenLineJoin.Round,
                                LineCap = PenLineCap.Round,
                            };
                            context.DrawGeometry(null, halo, geometria);
                        }

                        context.DrawGeometry(relleno, contorno, geometria);

                        // Y el filo interior, que es lo que en un neón de verdad se ve casi blanco.
                        if (_brillo > 0.4)
                        {
                            var filo = new Pen(new SolidColorBrush(Color.FromRgb(255, 250, 228),
                                                                   (_brillo - 0.4) * 0.95),
                                               Math.Max(1, size / 32));
                            context.DrawGeometry(null, filo, geometria);
                        }
                    }
                }

                x += anchos[i];
            }
        }

        /// <summary>Un color entre los dos, con <paramref name="cuanto"/> de 0 a 1.</summary>
        private static Color Mezclar(Color a, Color b, double cuanto)
        {
            double t = Math.Clamp(cuanto, 0, 1);
            return Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }

        private static FormattedText Medir(char c, Typeface face, double size) => new FormattedText(
            c.ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size,
            Brushes.White);
    }
}
