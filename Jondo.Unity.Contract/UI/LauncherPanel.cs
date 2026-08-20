using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// Translucent panel with rounded corners, the equivalent of the .launcher-box and
    /// .console-box blocks of the web interface.
    ///
    /// WinForms has no real transparency, so the panel cuts out the piece of background that sits
    /// behind it from the image already composed by the window and paints its color layers on top.
    /// That is how the glass effect over the background artwork is preserved.
    /// </summary>
    public class LauncherPanel : Panel
    {
        /// <summary>Color layers painted one over another on top of the background.</summary>
        public List<Color> Layers { get; } = new List<Color>();

        /// <summary>Radius of the rounded corners.</summary>
        public int CornerRadius { get; set; }

        /// <summary>Border color; transparent to skip drawing it.</summary>
        public Color BorderColor { get; set; } = Color.Transparent;

        /// <summary>Border thickness in pixels.</summary>
        public int BorderWidth { get; set; } = 2;

        /// <summary>Separator line at the bottom (the border-bottom of the bars).</summary>
        public Color BottomLine { get; set; } = Color.Transparent;

        /// <summary>Rounds only the top corners (top bars nested inside a card).</summary>
        public bool TopCornersOnly { get; set; }

        public LauncherPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // The background is painted entirely in OnPaint.
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var area = new Rectangle(0, 0, Width, Height);

            PaintClippedBackground(g);

            using (var path = BuildPath(area, CornerRadius))
            {
                foreach (var layer in Layers)
                {
                    using var brush = new SolidBrush(layer);
                    g.FillPath(brush, path);
                }

                if (BorderColor.A > 0)
                {
                    var frame = new Rectangle(BorderWidth / 2, BorderWidth / 2, Width - BorderWidth, Height - BorderWidth);
                    using var borderPath = BuildPath(frame, Math.Max(0, CornerRadius - BorderWidth / 2));
                    using var pen = new Pen(BorderColor, BorderWidth);
                    g.DrawPath(pen, borderPath);
                }
            }

            if (BottomLine.A > 0)
            {
                using var pen = new Pen(BottomLine, 1);
                g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            }

            base.OnPaint(e);
        }

        private GraphicsPath BuildPath(Rectangle area, int radius)
        {
            if (!TopCornersOnly) return LauncherTheme.RoundedPath(area, radius);

            var path = new GraphicsPath();
            int d = Math.Min(radius * 2, Math.Min(area.Width, area.Height));
            if (d <= 0)
            {
                path.AddRectangle(area);
                return path;
            }
            path.AddArc(area.X, area.Y, d, d, 180, 90);
            path.AddArc(area.Right - d, area.Y, d, d, 270, 90);
            path.AddLine(area.Right, area.Bottom, area.X, area.Bottom);
            path.CloseFigure();
            return path;
        }

        /// <summary>
        /// Copies the slice of the background image that sits right behind this panel.
        /// </summary>
        private void PaintClippedBackground(Graphics g)
        {
            // El fondo se pide por la interfaz —para que sirva en las tres ventanas que ya hay— pero
            // las coordenadas se piden al Form, que es quien las tiene. Son la misma instancia.
            var form = FindForm();
            var background = (form as IBackgroundWindow)?.ComposedBackground;
            if (form == null || background == null)
            {
                g.Clear(LauncherTheme.Background);
                return;
            }

            Point origin;
            try { origin = form.PointToClient(PointToScreen(Point.Empty)); }
            catch { g.Clear(LauncherTheme.Background); return; }

            var source = Rectangle.Intersect(new Rectangle(origin.X, origin.Y, Width, Height), new Rectangle(0, 0, background.Width, background.Height));
            g.Clear(LauncherTheme.Background);
            if (source.Width <= 0 || source.Height <= 0) return;

            var target = new Rectangle(source.X - origin.X, source.Y - origin.Y, source.Width, source.Height);
            g.DrawImage(background, target, source, GraphicsUnit.Pixel);
        }
    }
}
