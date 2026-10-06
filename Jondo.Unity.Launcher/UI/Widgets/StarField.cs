using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Jondo.Unity.Launcher.UI.Widgets
{
    /// <summary>
    /// Golden sparks falling in front of the background drawing.
    /// </summary>
    /// <remarks>
    /// It is what Windows Forms did not give without a fight: here it is a control that repaints itself
    /// twenty-five times a second and that is it.
    ///
    /// Each spark is three circles —a wide, faint halo, another short and bright one, and an almost
    /// white dot— and it twinkles at its own rhythm. What makes them look like light and not stains is the
    /// bright dot in the centre; with only the halos they look dirty.
    ///
    /// Two numbers rule over everything else and are in sight to be able to move them: how many there are
    /// and how fast they fall. Even so, this is a background: if it draws attention ahead of the
    /// login card, it has gone too far.
    ///
    /// The clock starts on entering the window and stops on leaving. Without that, a closed window
    /// would leave a timer repainting something that no longer exists, and in the screenless tests it would
    /// keep spinning forever.
    /// </remarks>
    internal sealed class StarField : Control
    {
        private sealed class Chispa
        {
            public double X;
            public double Y;
            public double Radio;
            public double Caida;      // pixels per second
            public double Deriva;     // how much it drifts sideways
            public double Fase;       // where its twinkle starts
            public double Ritmo;      // how fast it twinkles
        }

        /// <summary>How many. Seventy look like dust; two hundred would be a snowfall.</summary>
        private const int Cuantas = 70;

        private readonly Chispa[] _chispas = new Chispa[Cuantas];
        private readonly Random _azar = new Random(20260830);
        private readonly DispatcherTimer _reloj = new() { Interval = TimeSpan.FromMilliseconds(40) };
        private DateTime _ultimo = DateTime.UtcNow;
        private double _tiempo;

        public StarField()
        {
            IsHitTestVisible = false;
            for (int i = 0; i < Cuantas; i++) _chispas[i] = Nueva(true);
            _reloj.Tick += (_, _) => Latir();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _ultimo = DateTime.UtcNow;
            _reloj.Start();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _reloj.Stop();
            base.OnDetachedFromVisualTree(e);
        }

        private Chispa Nueva(bool repartidaPorTodaLaPantalla)
        {
            return new Chispa
            {
                X = _azar.NextDouble(),
                // At the start they are spread out; the ones born later come in from the top.
                Y = repartidaPorTodaLaPantalla ? _azar.NextDouble() : -0.02,
                Radio = 0.9 + _azar.NextDouble() * 1.9,
                // Faster than at first: at eight pixels per second one could barely see that they
                // fell, and a snowfall that does not move is dust on the screen.
                Caida = 20 + _azar.NextDouble() * 46,
                Deriva = (_azar.NextDouble() - 0.5) * 10,
                Fase = _azar.NextDouble() * Math.PI * 2,
                Ritmo = 0.8 + _azar.NextDouble() * 1.6,
            };
        }

        private void Latir()
        {
            var ahora = DateTime.UtcNow;
            double segundos = Math.Min(0.1, (ahora - _ultimo).TotalSeconds);
            _ultimo = ahora;
            _tiempo += segundos;

            double alto = Math.Max(1, Bounds.Height);
            double ancho = Math.Max(1, Bounds.Width);

            for (int i = 0; i < _chispas.Length; i++)
            {
                var c = _chispas[i];
                c.Y += c.Caida * segundos / alto;
                c.X += c.Deriva * segundos / ancho;

                if (c.Y > 1.02) _chispas[i] = Nueva(false);
                else if (c.X < -0.02) c.X += 1.04;
                else if (c.X > 1.02) c.X -= 1.04;
            }

            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            if (Bounds.Width <= 0 || Bounds.Height <= 0) return;

            var oro = LauncherSkin.LightGold;

            foreach (var c in _chispas)
            {
                // The twinkle: between half light and full, never completely off.
                double brillo = 0.55 + 0.45 * (1 + Math.Sin(_tiempo * c.Ritmo + c.Fase)) / 2;
                var centro = new Point(c.X * Bounds.Width, c.Y * Bounds.Height);

                // Three layers: a wide, faint halo, another short and brighter one, and the almost white dot
                // in the centre. With two they looked like stains; the bright dot is what makes them
                // look like light.
                context.DrawEllipse(new SolidColorBrush(oro, brillo * 0.20), null,
                                    centro, c.Radio * 4.2, c.Radio * 4.2);
                context.DrawEllipse(new SolidColorBrush(oro, brillo * 0.45), null,
                                    centro, c.Radio * 2.0, c.Radio * 2.0);
                context.DrawEllipse(new SolidColorBrush(Color.FromRgb(255, 250, 225), brillo), null,
                                    centro, c.Radio, c.Radio);
            }
        }
    }
}
