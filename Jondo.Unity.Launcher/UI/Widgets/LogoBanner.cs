using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Jondo.Unity.Launcher.UI.Widgets
{
    /// <summary>
    /// The «JONDO EMU» sign, lit like a neon tube.
    /// </summary>
    /// <remarks>
    /// It is drawn, not an image: Ankama's logo lettering cannot be re-lettered, so
    /// what is done is a sign of our own with the same feel —gold with a gradient, a thick dark
    /// outline and a little arc— using the launcher's typeface. The outline comes from the
    /// text's geometry: <c>BuildGeometry</c> gives the letters' silhouette, and it is filled and
    /// stroked in the same pass.
    ///
    /// <b>Mind the brush.</b> <c>FormattedText.BuildGeometry</c> returns an EMPTY silhouette if the
    /// text has no brush set. With <c>null</c> it does not throw, does not warn and does not paint: the sign was
    /// invisible since the launcher moved to Avalonia and nobody noticed.
    /// </remarks>
    internal sealed class LogoBanner : Control
    {
        /// <summary>The two words. The launcher puts JONDO EMU and the server JONDO SERVER.</summary>
        public string First { get; init; } = "JONDO";
        public string Second { get; init; } = "EMU";

        /// <summary>How much the sign arches, in degrees of rotation of the first and last letter.</summary>
        private const double Arc = 7;

        /// <summary>Whether on appearing it does the tube's start-up. False: it comes out already lit.</summary>
        /// <remarks>
        /// It exists to be able to photograph it lit without waiting a second and a bit, which is what is
        /// done when working on the design. In the window it is not touched.
        /// </remarks>
        public bool ConArranque { get; init; } = true;

        // ═══════════════════════════════════════════════════════════════════
        //  The neon
        // ═══════════════════════════════════════════════════════════════════
        //
        // A neon tube does not switch on: it STARTS UP. It flashes, goes off, stutters a
        // few times ever closer together and ends up staying on. Once lit it is not completely
        // still: it trembles a little and now and then it goes for an instant.
        //
        // The start-up is written by hand in a table and does not come from any randomness, because it is a
        // CHOREOGRAPHY: the timings and the order of the flashes are what make it look like a real
        // tube, and leaving them to chance turns it into a broken light. What is random is
        // the flickering afterwards, which has to be unpredictable.

        /// <summary>The start-up: until which second each stretch lasts, and with how much light.</summary>
        private static readonly (double Hasta, double Luz)[] Arranque =
        {
            (0.12, 0.00),   // un instante a oscuras antes de nada
            (0.20, 1.00),   // the first flash
            (0.32, 0.04),
            (0.38, 0.85),
            (0.46, 0.02),
            (0.58, 0.00),   // it looks like it will not start
            (0.66, 1.00),
            (0.72, 0.08),
            (0.80, 0.95),
            (0.86, 0.14),
            (0.94, 1.00),
            (1.00, 0.32),   // the last stutter, already weak
            (1.20, 1.00),   // and it stays
        };

        /// <summary>How many halo layers. Six is where adding more stops being noticeable.</summary>
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

            // The start-up begins every time the sign comes on screen, not once per process.
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
            // The steady flicker: rare, short, and never fully dark. A tube that
            // went off entirely every so often would be broken, not lit.
            if (_tiempo > _apagonHasta && _azar.NextDouble() < 0.004)
            {
                _apagonHasta = _tiempo + 0.05 + _azar.NextDouble() * 0.10;
            }
            if (_tiempo < _apagonHasta) return 0.30;

            // And the tremble: two sines with periods that do not fit together. A single one looks mechanical
            // after three seconds of looking at it.
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

            // The fill goes up from dim gold to almost white with the light; fully off it stays at
            // the outline's brown, which is a tube without gas.
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
            // Thin on purpose: it is the dark edge separating the letter from the halo, not a border. Thick,
            // it ate the gold inside and the sign read as a hollow outline.
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

                        // The glow: the same silhouette stroked six times, each one fatter and
                        // more transparent. It is a poor man's halo, and at this size it cannot be told from
                        // a real one, which would cost a blur per frame.
                        for (int capa = Capas; capa >= 1; capa--)
                        {
                            // It falls off with the layer's SQUARE: that way the outer one is a veil and the
                            // inner one is the edge. Falling off linearly, the outer ones weighed as much as
                            // the inner ones and the halo ate the letters.
                            double alfa = _brillo * 0.26 / (capa * capa);
                            if (alfa < 0.004) continue;

                            var color = capa > Capas / 2 ? LauncherSkin.Gold : LauncherSkin.LightGold;
                            var halo = new Pen(new SolidColorBrush(color, alfa),
                                               contorno.Thickness + capa * size * 0.11)
                            {
                                // ROUND, both things. By default a thick stroke joins with a point,
                                // and at a letter's corners those are spikes: the glow
                                // came out like a spiky star instead of a halo.
                                LineJoin = PenLineJoin.Round,
                                LineCap = PenLineCap.Round,
                            };
                            context.DrawGeometry(null, halo, geometria);
                        }

                        context.DrawGeometry(relleno, contorno, geometria);

                        // And the inner edge, which is what in a real neon looks almost white.
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

        /// <summary>A colour between the two, with <paramref name="cuanto"/> from 0 to 1.</summary>
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
