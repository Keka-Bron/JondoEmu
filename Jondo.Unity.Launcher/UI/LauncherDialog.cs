using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// Un aviso con la cara del launcher.
    ///
    /// El MessageBox de Windows es blanco, con la fuente del sistema y el icono azul de siempre, y
    /// delante del fondo del launcher canta. Esto es lo mismo pero pintado con la paleta de
    /// <see cref="LauncherTheme"/>: fondo oscuro, borde dorado, la tipografía del launcher y el
    /// botón verde de aceptar.
    /// </summary>
    internal sealed class LauncherDialog : Form
    {
        private LauncherDialog(string title, string message, string accept)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = LauncherTheme.Background;
            ClientSize = new Size(460, 210);
            KeyPreview = true;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            var cabecera = new Label
            {
                Text = title,
                ForeColor = LauncherTheme.LightGold,
                BackColor = Color.Transparent,
                Font = LauncherTheme.CreateFont(17f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Bounds = new Rectangle(2, 16, ClientSize.Width - 4, 34),
            };

            var texto = new Label
            {
                Text = message,
                ForeColor = LauncherTheme.BaseText,
                BackColor = Color.Transparent,
                Font = LauncherTheme.CreateFont(14f),
                TextAlign = ContentAlignment.MiddleCenter,
                Bounds = new Rectangle(28, 60, ClientSize.Width - 56, 68),
            };

            var boton = new LauncherButton
            {
                Text = accept,
                Bounds = new Rectangle((ClientSize.Width - 190) / 2, 142, 190, 44),
                Font = LauncherTheme.CreateFont(15f, FontStyle.Bold),
                BackgroundTop = LauncherTheme.GreenTop,
                BackgroundBottom = LauncherTheme.GreenBottom,
                BackgroundTopHighlight = LauncherTheme.GreenTopHover,
                BackgroundBottomHighlight = LauncherTheme.GreenBottomHover,
                BorderColor = LauncherTheme.GreenBorder,
                BorderColorHighlight = LauncherTheme.GreenBorder,
                TextColor = Color.White,
                TextColorHighlight = Color.White,
                CornerRadius = 8,
                BorderWidth = 2,
            };
            boton.Click += (s, e) => Close();

            Controls.Add(cabecera);
            Controls.Add(texto);
            Controls.Add(boton);

            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Enter) Close();
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            var caja = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            using (var relleno = new SolidBrush(Color.FromArgb(246, 16, 9, 5)))
            using (var camino = Rounded(caja, 14))
            {
                e.Graphics.FillPath(relleno, camino);
                using var borde = new Pen(LauncherTheme.GoldBorder, 2f);
                e.Graphics.DrawPath(borde, camino);
            }

            // La raya de debajo del título, como la de las tarjetas.
            using var linea = new Pen(Color.FromArgb(120, LauncherTheme.BorderBrown), 1f);
            e.Graphics.DrawLine(linea, 24, 54, ClientSize.Width - 24, 54);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>Enseña el aviso y espera a que lo cierren.</summary>
        public static void Show(IWin32Window owner, string title, string message, string accept)
        {
            using var dialog = new LauncherDialog(title, message, accept);
            dialog.ShowDialog(owner);
        }
    }
}
