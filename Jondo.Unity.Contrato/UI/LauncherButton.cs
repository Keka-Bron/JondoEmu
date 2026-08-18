using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>Optional icon shown next to a button's text.</summary>
    public enum ButtonIcon
    {
        None,
        Speaker,
        Flag,
        CheckBox,
        Folder
    }

    /// <summary>
    /// Hand-drawn button. It covers every style of the web interface (.btn-action, .tab-btn,
    /// .music-btn, .lang-btn and .btn-clear) by configuring colors, corner radius and active state.
    /// </summary>
    public sealed class LauncherButton : Control
    {
        public Color BackgroundTop { get; set; } = Color.FromArgb(204, 35, 20, 12);
        public Color BackgroundBottom { get; set; } = Color.FromArgb(204, 35, 20, 12);
        public Color BackgroundTopHighlight { get; set; } = Color.FromArgb(89, 60, 29);
        public Color BackgroundBottomHighlight { get; set; } = Color.FromArgb(89, 60, 29);
        public Color BackgroundTopActive { get; set; } = Color.Empty;
        public Color BackgroundBottomActive { get; set; } = Color.Empty;

        public Color BorderColor { get; set; } = Color.Transparent;
        public Color BorderColorHighlight { get; set; } = Color.Transparent;
        public Color BorderColorActive { get; set; } = Color.Empty;

        public Color TextColor { get; set; } = LauncherTheme.SoftGold;
        public Color TextColorHighlight { get; set; } = Color.White;
        public Color TextColorActive { get; set; } = Color.Empty;

        public Color BackgroundTopDisabled { get; set; } = LauncherTheme.GrayTop;
        public Color BackgroundBottomDisabled { get; set; } = LauncherTheme.GrayBottom;
        public Color BorderColorDisabled { get; set; } = LauncherTheme.GrayBorder;
        public Color TextColorDisabled { get; set; } = LauncherTheme.GrayText;

        public int CornerRadius { get; set; } = 5;
        public int BorderWidth { get; set; } = 1;
        public float LetterSpacing { get; set; }
        public bool TopCornersOnly { get; set; }
        public bool TextShadow { get; set; }

        /// <summary>The "active" state (selected tab or chosen language).</summary>
        public bool Active
        {
            get => _active;
            set { if (_active != value) { _active = value; Invalidate(); } }
        }
        private bool _active;

        public Color Underline { get; set; } = Color.Transparent;
        public int UnderlineWidth { get; set; } = 3;

        public ButtonIcon Icon { get; set; } = ButtonIcon.None;
        public string FlagCode { get; set; } = "es";
        public bool IconMuted { get; set; }

        private bool _hovering;
        private bool _pressed;

        public LauncherButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e) { _hovering = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hovering = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            bool highlighted = Enabled && (_hovering || _pressed);
            Color top, bottom, border, text;

            if (!Enabled)
            {
                top = BackgroundTopDisabled; bottom = BackgroundBottomDisabled;
                border = BorderColorDisabled; text = TextColorDisabled;
            }
            else if (Active && BackgroundTopActive != Color.Empty)
            {
                top = BackgroundTopActive; bottom = BackgroundBottomActive;
                border = BorderColorActive != Color.Empty ? BorderColorActive : BorderColor;
                text = TextColorActive != Color.Empty ? TextColorActive : TextColor;
            }
            else if (highlighted)
            {
                top = BackgroundTopHighlight; bottom = BackgroundBottomHighlight;
                border = BorderColorHighlight.A > 0 ? BorderColorHighlight : BorderColor;
                text = TextColorHighlight;
            }
            else
            {
                top = BackgroundTop; bottom = BackgroundBottom;
                border = BorderColor; text = TextColor;
            }

            var area = new Rectangle(0, 0, Width, Height);
            using (var path = BuildBackgroundPath(area))
            {
                using var brush = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height) + 1), top, bottom, LinearGradientMode.Vertical);
                g.FillPath(brush, path);
            }

            if (border.A > 0 && BorderWidth > 0)
            {
                var frame = new Rectangle(BorderWidth / 2, BorderWidth / 2, Width - BorderWidth, Height - BorderWidth);
                using var borderPath = BuildBackgroundPath(frame);
                using var pen = new Pen(border, BorderWidth);
                g.DrawPath(pen, borderPath);
            }

            if (Active && Underline.A > 0)
            {
                using var brush = new SolidBrush(Underline);
                g.FillRectangle(brush, 0, Height - UnderlineWidth, Width, UnderlineWidth);
            }

            // Icon (music speaker or language flag) followed by the text.
            int offset = _pressed && Enabled ? 1 : 0;
            var content = new Rectangle(6, offset, Width - 12, Height);

            if (Icon != ButtonIcon.None)
            {
                int side = Math.Min(14, Height - 8);
                var iconArea = new Rectangle(content.X + 2, (Height - side) / 2 + offset, Icon == ButtonIcon.Flag ? side + 4 : side, side);

                if (Icon == ButtonIcon.Speaker) LauncherTheme.DrawSpeaker(g, iconArea, text, IconMuted);
                else if (Icon == ButtonIcon.Flag) LauncherTheme.DrawFlag(g, iconArea, FlagCode);
                else if (Icon == ButtonIcon.Folder) LauncherTheme.DrawFolder(g, iconArea, text);
                else DrawCheckBox(g, iconArea, text);

                content = new Rectangle(iconArea.Right + 5, offset, Width - (iconArea.Right + 5) - 6, Height);
            }

            if (!string.IsNullOrEmpty(Text))
            {
                var alignment = Icon == ButtonIcon.None ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft;
                if (TextShadow)
                {
                    var shadow = new Rectangle(content.X, content.Y + 1, content.Width, content.Height);
                    LauncherTheme.DrawSpacedText(g, Text, Font, Color.FromArgb(170, 0, 0, 0), shadow, LetterSpacing, alignment);
                }
                LauncherTheme.DrawSpacedText(g, Text, Font, text, content, LetterSpacing, alignment);
            }
        }

        /// <summary>Check box equivalent to the input[type=checkbox] of the web version.</summary>
        private void DrawCheckBox(Graphics g, Rectangle area, Color color)
        {
            int side = Math.Min(area.Width, area.Height) - 2;
            var box = new Rectangle(area.X, area.Y + (area.Height - side) / 2, side, side);

            using (var background = new SolidBrush(Color.FromArgb(200, 12, 6, 3)))
            {
                g.FillRectangle(background, box);
            }
            using (var pen = new Pen(color, 1))
            {
                g.DrawRectangle(pen, box);
            }

            if (!Active) return;
            using var check = new Pen(LauncherTheme.LightGold, Math.Max(1.6f, side / 6f));
            g.DrawLines(check, new[]
            {
                new PointF(box.X + side * 0.22f, box.Y + side * 0.52f),
                new PointF(box.X + side * 0.44f, box.Y + side * 0.76f),
                new PointF(box.X + side * 0.80f, box.Y + side * 0.24f)
            });
        }

        private GraphicsPath BuildBackgroundPath(Rectangle area)
        {
            if (!TopCornersOnly) return LauncherTheme.RoundedPath(area, CornerRadius);

            var path = new GraphicsPath();
            int d = Math.Min(CornerRadius * 2, Math.Min(area.Width, area.Height));
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
    }
}
