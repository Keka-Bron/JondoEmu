using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// El rótulo de "JONDO EMU" que va encima de la tarjeta de acceso.
    ///
    /// Está dibujado aquí, no es una imagen: el trazo del logotipo de Ankama no se puede reletrar,
    /// así que lo que se hace es un rótulo propio con el mismo aire —oro con degradado, contorno
    /// oscuro grueso, un poco de arco y una sombra debajo— usando la tipografía del launcher.
    ///
    /// Se pinta sobre el fondo recortado de la ventana, igual que los paneles, para que el dibujo
    /// de detrás se vea a través de los huecos de las letras.
    /// </summary>
    public sealed class LauncherLogo : Panel
    {
        /// <summary>Las dos palabras del rotulo. El lanzador pone JONDO EMU y el servidor JONDO SERVER.</summary>
        public string Primera { get; set; } = "JONDO";
        public string Segunda { get; set; } = "EMU";

        /// <summary>Cuánto se arquea el rótulo, en grados de giro de la primera y la última letra.</summary>
        private const float Arco = 7f;

        public LauncherLogo()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            TabStop = false;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // El fondo se pinta entero en OnPaint, igual que en los paneles.
        }

        /// <summary>
        /// Recorta del fondo ya compuesto por la ventana el trozo que hay detrás, que es como los
        /// paneles consiguen verse translúcidos. WinForms no tiene transparencia de verdad.
        /// </summary>
        private void PintarFondo(Graphics g)
        {
            var ventana = FindForm() as IVentanaConFondo;
            var fondo = ventana?.ComposedBackground;
            var control = ventana as Control;
            if (control == null || fondo == null) { g.Clear(LauncherTheme.Background); return; }

            Point origen;
            try { origen = control.PointToClient(PointToScreen(Point.Empty)); }
            catch { g.Clear(LauncherTheme.Background); return; }

            var recorte = Rectangle.Intersect(new Rectangle(origen.X, origen.Y, Width, Height),
                                              new Rectangle(0, 0, fondo.Width, fondo.Height));
            g.Clear(LauncherTheme.Background);
            if (recorte.Width <= 0 || recorte.Height <= 0) return;

            var destino = new Rectangle(recorte.X - origen.X, recorte.Y - origen.Y,
                                        recorte.Width, recorte.Height);
            g.DrawImage(fondo, destino, recorte, GraphicsUnit.Pixel);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            PintarFondo(g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            float alto = Height * 0.52f;
            using var fuenteGrande = new Font(LauncherTheme.TitleFamily, alto, FontStyle.Bold,
                                              GraphicsUnit.Pixel);
            using var fuentePequena = new Font(LauncherTheme.TitleFamily, alto * 0.52f,
                                               FontStyle.Bold, GraphicsUnit.Pixel);

            using var camino = new GraphicsPath();
            using var formato = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };

            // JONDO grande y arqueado, EMU debajo y a la derecha, como una segunda línea.
            float centro = Width / 2f;
            camino.AddString(Primera, new FontFamily(LauncherTheme.TitleFamily), (int)FontStyle.Bold,
                             alto, new PointF(centro, Height * 0.36f), formato);
            camino.AddString(Segunda, new FontFamily(LauncherTheme.TitleFamily), (int)FontStyle.Bold,
                             alto * 0.46f, new PointF(centro, Height * 0.79f), formato);

            var caja = camino.GetBounds();
            if (caja.Width <= 0 || caja.Height <= 0) return;

            // El arco: se inclina un pelín el conjunto, que es lo que le da el aire de rótulo.
            using var giro = new Matrix();
            giro.RotateAt(-Arco * 0.35f, new PointF(centro, Height / 2f));
            camino.Transform(giro);
            caja = camino.GetBounds();

            // La sombra.
            using (var sombra = new GraphicsPath())
            {
                sombra.AddPath(camino, false);
                using var abajo = new Matrix();
                abajo.Translate(0, Math.Max(2f, alto * 0.06f));
                sombra.Transform(abajo);
                using var pincelSombra = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
                g.FillPath(pincelSombra, sombra);
            }

            // El contorno, en dos pasadas: una marrón muy oscura y ancha y otra dorada fina.
            using (var fuera = new Pen(Color.FromArgb(235, 38, 20, 6), Math.Max(5f, alto * 0.17f))
                   { LineJoin = LineJoin.Round })
            {
                g.DrawPath(fuera, camino);
            }
            using (var dentro = new Pen(Color.FromArgb(255, LauncherTheme.BorderBrown), Math.Max(2f, alto * 0.06f))
                   { LineJoin = LineJoin.Round })
            {
                g.DrawPath(dentro, camino);
            }

            // El relleno: oro claro arriba, ámbar abajo, con un destello a un tercio de altura.
            using (var oro = new LinearGradientBrush(
                       new RectangleF(caja.X, caja.Y, caja.Width, caja.Height + 1),
                       Color.FromArgb(255, 255, 236, 160),
                       Color.FromArgb(255, 176, 108, 20),
                       LinearGradientMode.Vertical))
            {
                oro.InterpolationColors = new ColorBlend
                {
                    Colors = new[]
                    {
                        Color.FromArgb(255, 255, 245, 200),
                        Color.FromArgb(255, 255, 214, 92),
                        Color.FromArgb(255, 226, 158, 40),
                        Color.FromArgb(255, 168, 100, 16),
                    },
                    Positions = new[] { 0f, 0.36f, 0.62f, 1f },
                };
                g.FillPath(oro, camino);
            }

            // Y un brillo por encima de la mitad de arriba de las letras.
            var mitad = new RectangleF(caja.X, caja.Y, caja.Width, caja.Height * 0.42f);
            using (var recorte = new Region(camino))
            {
                var antes = g.Clip;
                g.Clip = recorte;
                using var brillo = new LinearGradientBrush(mitad,
                    Color.FromArgb(150, 255, 255, 255), Color.FromArgb(0, 255, 255, 255),
                    LinearGradientMode.Vertical);
                g.FillRectangle(brillo, mitad);
                g.Clip = antes;
            }
        }
    }
}
