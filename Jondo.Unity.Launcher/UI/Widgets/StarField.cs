using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Jondo.Unity.Launcher.UI.Widgets
{
    /// <summary>
    /// Chispas doradas cayendo por delante del dibujo del fondo.
    /// </summary>
    /// <remarks>
    /// Es lo que Windows Forms no daba sin pelearse: aquí es un control que se repinta solo
    /// veinticinco veces por segundo y ya está.
    ///
    /// Cada chispa son tres círculos —un halo ancho y flojo, otro corto y vivo, y un punto casi
    /// blanco— y parpadea a su propio ritmo. Lo que las hace parecer luz y no manchas es el punto
    /// claro del centro; con sólo los halos se ven sucias.
    ///
    /// Dos números mandan sobre todo lo demás y están a la vista para poder moverlos: cuántas son
    /// y a qué velocidad caen. Aun así, esto es un fondo: si se lleva la atención por delante de la
    /// tarjeta de acceso, se ha pasado.
    ///
    /// El reloj arranca al entrar en la ventana y se para al salir. Sin eso, una ventana cerrada
    /// dejaría un temporizador repintando algo que ya no existe, y en las pruebas sin pantalla se
    /// quedaría girando para siempre.
    /// </remarks>
    internal sealed class StarField : Control
    {
        private sealed class Chispa
        {
            public double X;
            public double Y;
            public double Radio;
            public double Caida;      // píxeles por segundo
            public double Deriva;     // lo que se va de lado
            public double Fase;       // dónde empieza su parpadeo
            public double Ritmo;      // cómo de rápido parpadea
        }

        /// <summary>Cuántas. Setenta se ven como polvo; doscientas serían una nevada.</summary>
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
                // Al empezar están repartidas; las que nacen luego entran por arriba.
                Y = repartidaPorTodaLaPantalla ? _azar.NextDouble() : -0.02,
                Radio = 0.9 + _azar.NextDouble() * 1.9,
                // Más rápidas que al principio: a ocho píxeles por segundo apenas se veía que
                // cayeran, y una nevada que no se mueve es polvo en la pantalla.
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
                // El parpadeo: entre media luz y el total, nunca apagada del todo.
                double brillo = 0.55 + 0.45 * (1 + Math.Sin(_tiempo * c.Ritmo + c.Fase)) / 2;
                var centro = new Point(c.X * Bounds.Width, c.Y * Bounds.Height);

                // Tres capas: un halo ancho y flojo, otro corto y más vivo, y el punto casi blanco
                // en el centro. Con dos se veían como manchas; el punto claro es lo que las hace
                // parecer luz.
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
