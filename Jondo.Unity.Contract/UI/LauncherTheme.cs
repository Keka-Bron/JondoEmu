using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// Palette, typefaces and drawing helpers for the launcher.
    ///
    /// Every value comes from the stylesheet of the previous web interface
    /// (launcher_assets/index.html), translated into GDI+ colors and measurements so that the
    /// native window is recognizable as the very same launcher.
    /// </summary>
    public static class LauncherTheme
    {
        // ─── Colors ─────────────────────────────────────────────────────────────
        /// <summary>Window background color (#0d0603), visible when the image is missing.</summary>
        public static readonly Color Background = Color.FromArgb(13, 6, 3);

        // Las dos tarjetas van bastante más transparentes que en la web original —de 0,84 y 0,86 a
        // 0,52 y 0,55— para que se vea el dibujo del fondo por detrás. Lo que hay encima sigue
        // leyéndose porque el texto es claro sobre un marrón muy oscuro.
        /// <summary>Translucent fill of the cards.</summary>
        public static readonly Color CardFill = Color.FromArgb(133, 16, 9, 5);

        /// <summary>Translucent fill of the console panel.</summary>
        public static readonly Color ConsoleFill = Color.FromArgb(140, 12, 7, 4);

        /// <summary>Top bar of the cards: rgba(26, 15, 8, 0.75).</summary>
        public static readonly Color BarFill = Color.FromArgb(191, 26, 15, 8);

        /// <summary>
        /// El cuerpo del registro. Va OPACO a la fuerza: es un RichTextBox y a un control de texto
        /// de Windows no se le puede poner un color con alfa —lanza "el control no admite colores de
        /// fondo transparentes" y se lleva por delante la ventana entera—. Lo que sí es translúcido
        /// es el panel que lo rodea. Este tono es el que resulta de aquel negro al 66% sobre el
        /// fondo, para que no parezca un agujero.
        /// </summary>
        public static readonly Color ConsoleBackground = Color.FromArgb(24, 16, 10);

        public static readonly Color GoldBorder = Color.FromArgb(230, 184, 0);   // #e6b800
        public static readonly Color LightGold = Color.FromArgb(255, 204, 0);      // #ffcc00
        public static readonly Color Gold = Color.FromArgb(212, 175, 55);          // #d4af37
        public static readonly Color SoftGold = Color.FromArgb(230, 194, 128);    // #e6c280
        public static readonly Color MutedGold = Color.FromArgb(184, 152, 101);  // #b89865
        public static readonly Color LightBrown = Color.FromArgb(89, 60, 29);    // #593c1d
        /// <summary>Para lo que se enseña pero no cambia: se lee sin robar atención.</summary>
        public static readonly Color LightBrownText = Color.FromArgb(150, 126, 92);
        public static readonly Color BorderBrown = Color.FromArgb(122, 83, 40);   // #7a5328
        public static readonly Color BaseText = Color.FromArgb(255, 243, 214);   // #fff3d6
        public static readonly Color CardText = Color.FromArgb(255, 243, 204);// #fff3cc
        public static readonly Color HighlightText = Color.FromArgb(255, 230, 128); // #ffe680
        public static readonly Color FieldText = Color.FromArgb(255, 248, 231);  // #fff8e7

        public static readonly Color FieldBackground = Color.FromArgb(217, 12, 6, 3);          // rgba(12,6,3,0.85)
        public static readonly Color DisabledFieldBackground = Color.FromArgb(128, 20, 10, 5); // rgba(20,10,5,0.5)
        public static readonly Color DisabledFieldBorder = Color.FromArgb(68, 48, 26);     // #44301a
        public static readonly Color DisabledFieldText = Color.FromArgb(119, 102, 85);   // #776655

        public static readonly Color GreenTop = Color.FromArgb(125, 179, 38);  // #7db326
        public static readonly Color GreenBottom = Color.FromArgb(70, 108, 20);    // #466c14
        public static readonly Color GreenBorder = Color.FromArgb(163, 224, 59);   // #a3e03b
        public static readonly Color GreenTopHover = Color.FromArgb(145, 206, 44);
        public static readonly Color GreenBottomHover = Color.FromArgb(85, 131, 25);

        public static readonly Color PurpleTop = Color.FromArgb(160, 64, 160); // #a040a0
        public static readonly Color PurpleBottom = Color.FromArgb(96, 32, 96);    // #602060
        public static readonly Color PurpleBorder = Color.FromArgb(208, 112, 208); // #d070d0

        public static readonly Color GrayTop = Color.FromArgb(68, 68, 68);
        public static readonly Color GrayBottom = Color.FromArgb(34, 34, 34);
        public static readonly Color GrayBorder = Color.FromArgb(85, 85, 85);
        public static readonly Color GrayText = Color.FromArgb(136, 136, 136);

        public static readonly Color Red = Color.FromArgb(255, 77, 77);          // #ff4d4d
        public static readonly Color OnlineGreen = Color.FromArgb(146, 208, 80); // #92d050
        public static readonly Color DotGreen = Color.FromArgb(80, 255, 80);    // #50ff50
        public static readonly Color AlertBackground = Color.FromArgb(230, 140, 20, 20); // rgba(140,20,20,0.9)
        public static readonly Color AlertText = Color.FromArgb(255, 230, 230); // #ffe6e6

        // Event log colors (the .log-* classes of the web interface).
        public static readonly Color LogHaapi = Color.FromArgb(0, 240, 255);
        public static readonly Color LogZaap = Color.FromArgb(51, 204, 255);
        public static readonly Color LogServer = Color.FromArgb(255, 204, 0);
        public static readonly Color LogSuccess = Color.FromArgb(80, 255, 80);
        public static readonly Color LogError = Color.FromArgb(255, 77, 77);
        public static readonly Color LogNormal = Color.FromArgb(209, 186, 134);
        public static readonly Color LogTime = Color.FromArgb(122, 101, 72);

        // ─── Typefaces ──────────────────────────────────────────────────────────

        private static string? _titleFamily;
        private static string? _monoFamily;

        /// <summary>
        /// Same fallback chain as the web version: Cinzel if it is installed, otherwise
        /// Trebuchet MS and finally the system UI font.
        /// </summary>
        public static string TitleFamily => _titleFamily ??= FirstAvailable("Cinzel", "Trebuchet MS", "Segoe UI");

        /// <summary>Consolas / Courier New for the event log.</summary>
        public static string MonoFamily => _monoFamily ??= FirstAvailable("Consolas", "Courier New");

        private static string FirstAvailable(params string[] names)
        {
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var collection = new InstalledFontCollection();
                foreach (var family in collection.Families) installed.Add(family.Name);
            }
            catch { }

            foreach (string name in names)
            {
                if (installed.Count == 0 || installed.Contains(name)) return name;
            }
            return FontFamily.GenericSansSerif.Name;
        }

        /// <summary>Creates a font from a size given in CSS pixels (1 px = 0.75 pt).</summary>
        public static Font CreateFont(float pixels, FontStyle style = FontStyle.Regular)
            => new Font(TitleFamily, pixels * 0.75f * UiZoom, style, GraphicsUnit.Point);

        /// <summary>Monospaced font of the event log, sized in CSS pixels.</summary>
        public static Font CreateMonoFont(float pixels, FontStyle style = FontStyle.Regular)
            => new Font(MonoFamily, pixels * 0.75f * UiZoom, style, GraphicsUnit.Point);

        // ─── El tamaño de la interfaz ─────────────────────────────────────────────────────────
        //
        // Con una pantalla de muchas pulgadas al 100% de escala, Windows reporta 96 dpi y la
        // interfaz se dibuja al tamaño de entonces, que en pixeles fisicos pequeñitos se queda
        // minúscula. El ampliador es un multiplicador ENCIMA del escalado por dpi: quien lo necesita
        // lo sube y quien no lo deja en 1. Lo aplican las dos ventanas —lanzador y servidor— cada
        // una con su preferencia guardada, y de meterlo aquí se encargan los dos a la vez porque
        // los dos pintan sus letras con estas dos fabricas.
        private static float _uiZoom = 1f;

        /// <summary>Cuánto se amplía la interfaz, además del escalado por dpi. 1 = sin ampliar.</summary>
        public static float UiZoom
        {
            get => _uiZoom;
            set => _uiZoom = MathF.Max(0.5f, MathF.Min(3f, value));
        }

        // ─── Drawing helpers ────────────────────────────────────────────────────

        /// <summary>Path of a rectangle with rounded corners.</summary>
        public static GraphicsPath RoundedPath(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0 || r.Width <= 0 || r.Height <= 0)
            {
                path.AddRectangle(r);
                return path;
            }

            int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>Fills a rounded rectangle with a vertical gradient.</summary>
        public static void FillGradient(Graphics g, Rectangle r, Color top, Color bottom, int radius)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using var path = RoundedPath(r, radius);
            using var brush = new LinearGradientBrush(new Rectangle(r.X, r.Y, r.Width, r.Height + 1), top, bottom, LinearGradientMode.Vertical);
            g.FillPath(brush, path);
        }

        /// <summary>
        /// Draws text spreading extra space between letters, like the letter-spacing of the
        /// stylesheet. GDI+ has no built-in support for it, so the text is painted letter by letter.
        /// </summary>
        public static void DrawSpacedText(Graphics g, string text, Font font, Color color, Rectangle area, float spacing, ContentAlignment alignment = ContentAlignment.MiddleCenter)
        {
            if (string.IsNullOrEmpty(text)) return;

            var format = StringFormat.GenericTypographic;
            float height = font.GetHeight(g);

            // With no extra spacing the text is drawn in one go: splitting it letter by letter
            // would shift the characters around because of pixel-snapping rounding.
            if (spacing <= 0.01f)
            {
                float fullWidth = MeasureSpacedText(g, text, font, 0f);
                float plainLeft = PlaceX(alignment, area, fullWidth);
                float plainTop = PlaceY(alignment, area, height);
                using var plainBrush = new SolidBrush(color);
                g.DrawString(text, font, plainBrush, plainLeft, plainTop, format);
                return;
            }

            float width = 0f;
            var widths = new float[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                widths[i] = CharacterWidth(g, text[i], font, format);
                width += widths[i] + spacing;
            }
            if (text.Length > 0) width -= spacing;

            float x = PlaceX(alignment, area, width);
            float y = PlaceY(alignment, area, height);

            using var brush = new SolidBrush(color);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != ' ') g.DrawString(text[i].ToString(), font, brush, x, y, format);
                x += widths[i] + spacing;
            }
        }

        /// <summary>Width a text will take up once letter spacing is applied.</summary>
        public static float MeasureSpacedText(Graphics g, string text, Font font, float spacing)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            var format = StringFormat.GenericTypographic;
            float width = 0f;
            foreach (char c in text) width += CharacterWidth(g, c, font, format) + spacing;
            return width - spacing;
        }

        /// <summary>
        /// Width of a single character. The space is measured between two letters because GDI+
        /// trims stand-alone spaces, which would leave the text cramped together.
        /// </summary>
        private static float CharacterWidth(Graphics g, char character, Font font, StringFormat format)
        {
            if (character != ' ') return g.MeasureString(character.ToString(), font, PointF.Empty, format).Width;
            return g.MeasureString("i i", font, PointF.Empty, format).Width
                 - g.MeasureString("ii", font, PointF.Empty, format).Width;
        }

        private static float PlaceX(ContentAlignment alignment, Rectangle area, float width) => alignment switch
        {
            ContentAlignment.MiddleLeft or ContentAlignment.TopLeft or ContentAlignment.BottomLeft => area.X,
            ContentAlignment.MiddleRight or ContentAlignment.TopRight or ContentAlignment.BottomRight => area.Right - width,
            _ => area.X + (area.Width - width) / 2f
        };

        private static float PlaceY(ContentAlignment alignment, Rectangle area, float height) => alignment switch
        {
            ContentAlignment.TopLeft or ContentAlignment.TopCenter or ContentAlignment.TopRight => area.Y,
            ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight => area.Bottom - height,
            _ => area.Y + (area.Height - height) / 2f
        };

        /// <summary>
        /// Hand-drawn speaker. GDI+ will not render the 🔊/🔇 emoji of the web launcher in color,
        /// so they are replaced by an equivalent vector icon.
        /// </summary>
        /// <summary>
        /// Una carpeta abierta, para el botón que elige dónde está el cliente. Dibujada a mano como
        /// el resto de iconos: la ventana no carga ninguna imagen que no esté en launcher_assets.
        /// </summary>
        public static void DrawFolder(Graphics g, Rectangle r, Color color)
        {
            var previousSmoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using var brush = new SolidBrush(color);
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;

            // La pestaña de arriba a la izquierda y el cuerpo, como una carpeta de toda la vida.
            g.FillRectangle(brush, x, y + h * 0.16f, w * 0.42f, h * 0.16f);
            using (var cuerpo = new GraphicsPath())
            {
                float top = y + h * 0.28f;
                cuerpo.AddLine(x, top, x + w, top);
                cuerpo.AddLine(x + w, top, x + w * 0.88f, y + h * 0.86f);
                cuerpo.AddLine(x + w * 0.88f, y + h * 0.86f, x + w * 0.12f, y + h * 0.86f);
                cuerpo.CloseFigure();
                g.FillPath(brush, cuerpo);
            }

            g.SmoothingMode = previousSmoothing;
        }

        public static void DrawSpeaker(Graphics g, Rectangle r, Color color, bool muted)
        {
            var previousSmoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using var brush = new SolidBrush(color);
            using var pen = new Pen(color, Math.Max(1f, r.Height / 9f));

            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            g.FillRectangle(brush, x, y + h * 0.33f, w * 0.30f, h * 0.34f);

            var cone = new PointF[]
            {
                new PointF(x + w * 0.28f, y + h * 0.33f),
                new PointF(x + w * 0.55f, y + h * 0.10f),
                new PointF(x + w * 0.55f, y + h * 0.90f),
                new PointF(x + w * 0.28f, y + h * 0.67f)
            };
            g.FillPolygon(brush, cone);

            if (muted)
            {
                g.DrawLine(pen, x + w * 0.66f, y + h * 0.30f, x + w * 0.95f, y + h * 0.70f);
                g.DrawLine(pen, x + w * 0.95f, y + h * 0.30f, x + w * 0.66f, y + h * 0.70f);
            }
            else
            {
                g.DrawArc(pen, x + w * 0.45f, y + h * 0.26f, w * 0.35f, h * 0.48f, -60, 120);
                g.DrawArc(pen, x + w * 0.40f, y + h * 0.10f, w * 0.58f, h * 0.80f, -60, 120);
            }

            g.SmoothingMode = previousSmoothing;
        }

        /// <summary>
        /// Simplified flags for the language selector: the flag emoji have no glyph on Windows
        /// either, so they are drawn with rectangles.
        /// </summary>
        public static void DrawFlag(Graphics g, Rectangle r, string code)
        {
            switch (code)
            {
                case "es":
                    using (var red = new SolidBrush(Color.FromArgb(198, 11, 30)))
                    using (var yellow = new SolidBrush(Color.FromArgb(255, 196, 0)))
                    {
                        g.FillRectangle(red, r);
                        g.FillRectangle(yellow, r.X, r.Y + r.Height * 0.25f, r.Width, r.Height * 0.5f);
                    }
                    break;

                case "fr":
                    using (var blue = new SolidBrush(Color.FromArgb(0, 85, 164)))
                    using (var white = new SolidBrush(Color.White))
                    using (var red = new SolidBrush(Color.FromArgb(239, 65, 53)))
                    {
                        g.FillRectangle(blue, r.X, r.Y, r.Width / 3f, r.Height);
                        g.FillRectangle(white, r.X + r.Width / 3f, r.Y, r.Width / 3f, r.Height);
                        g.FillRectangle(red, r.X + 2f * r.Width / 3f, r.Y, r.Width / 3f, r.Height);
                    }
                    break;

                default: // en
                    using (var blue = new SolidBrush(Color.FromArgb(1, 33, 105)))
                    using (var white = new Pen(Color.White, Math.Max(2f, r.Height / 4f)))
                    using (var red = new Pen(Color.FromArgb(200, 16, 46), Math.Max(1f, r.Height / 8f)))
                    {
                        g.FillRectangle(blue, r);
                        g.DrawLine(white, r.X, r.Y + r.Height / 2f, r.Right, r.Y + r.Height / 2f);
                        g.DrawLine(white, r.X + r.Width / 2f, r.Y, r.X + r.Width / 2f, r.Bottom);
                        g.DrawLine(red, r.X, r.Y + r.Height / 2f, r.Right, r.Y + r.Height / 2f);
                        g.DrawLine(red, r.X + r.Width / 2f, r.Y, r.X + r.Width / 2f, r.Bottom);
                    }
                    break;
            }
            using var frame = new Pen(Color.FromArgb(120, 0, 0, 0));
            g.DrawRectangle(frame, r);
        }

        /// <summary>Folder holding the launcher images and music.</summary>
        public static string AssetsFolder => Path.Combine(Paths.Root, "launcher_assets");

        /// <summary>Loads an image from launcher_assets; returns null when it is missing.</summary>
        public static Image? LoadImage(string name)
        {
            try
            {
                string path = Path.Combine(AssetsFolder, name);
                if (!File.Exists(path)) return null;
                // Read into memory so the file is not left locked.
                byte[] data = File.ReadAllBytes(path);
                using var memory = new MemoryStream(data);
                return Image.FromStream(memory);
            }
            catch
            {
                return null;
            }
        }
    }
}
