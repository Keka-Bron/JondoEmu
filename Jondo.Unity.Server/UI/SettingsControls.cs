using Jondo.Unity.Launcher.UI;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace Jondo.Unity.Server.UI
{
    /// <summary>The pieces of the Settings window, drawn in the launcher's colours instead of Windows' grey.</summary>
    internal static class SettingsPaint
    {
        /// <summary>The solid tone of a field over a card (rgba(12, 6, 3, 0.85), as LauncherField has it).</summary>
        public static readonly Color FieldFill = Color.FromArgb(13, 7, 4);

        /// <summary>The fill of a card, solid: the main window's translucent one over its dark background.</summary>
        public static readonly Color CardFill = Color.FromArgb(29, 17, 9);

        public static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Max(2, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>The golden glow of a focused field, as LauncherField draws it.</summary>
        public static void FocusGlow(Graphics g, GraphicsPath path, float scale)
        {
            using var halo = new Pen(Color.FromArgb(90, 255, 204, 0), Math.Max(3f, 3f * scale));
            g.DrawPath(halo, path);
        }

        /// <summary>Wrapped text's height in a width.</summary>
        public static int HeightOf(string text, Font font, int width)
            => string.IsNullOrEmpty(text) ? 0
               : TextRenderer.MeasureText(text, font, new Size(Math.Max(1, width), int.MaxValue),
                                          TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;

        public static void Text(Graphics g, string text, Font font, Color color, Rectangle box, bool wrap = true,
                                bool right = false)
        {
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.Top
                        | (wrap ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis)
                        | (right ? TextFormatFlags.Right : TextFormatFlags.Left);
            TextRenderer.DrawText(g, text, font, box, color, flags);
        }
    }

    /// <summary>
    /// A card of settings: a rounded box with its title in gold capitals, like the main window's
    /// WORLD and NETWORK, holding its rows one under the other.
    /// </summary>
    internal sealed class SettingsCard : Control
    {
        private readonly float _scale;
        private readonly Font _titleFont;
        private readonly List<Control> _rows = new();
        private int E(int px) => (int)Math.Round(px * _scale);

        public SettingsCard(string title, float scale)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Text = title;
            _scale = scale;
            _titleFont = LauncherTheme.CreateFont(12.5f, FontStyle.Bold);
        }

        public void Add(Control row)
        {
            _rows.Add(row);
            Controls.Add(row);
        }

        private int TitleHeight => E(12) + TextRenderer.MeasureText("W", _titleFont).Height + E(12);

        /// <summary>Lays the rows out in this width and gives the height they need.</summary>
        public int Arrange(int width)
        {
            int inner = width - E(18) * 2;
            int y = TitleHeight;
            foreach (var row in _rows)
            {
                int h = row is IMeasured measured ? measured.HeightFor(inner) : row.Height;
                row.SetBounds(E(18), y, inner, h);
                y += h + E(8);
            }
            return y - E(8) + E(14);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var box = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = SettingsPaint.Rounded(box, E(7)))
            using (var fill = new SolidBrush(SettingsPaint.CardFill))
            using (var border = new Pen(LauncherTheme.BorderBrown, Math.Max(1f, _scale)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            // The title, and a hairline under it that fades out to the right.
            int textHeight = TextRenderer.MeasureText("W", _titleFont).Height;
            SettingsPaint.Text(g, Text, _titleFont, LauncherTheme.SoftGold,
                               new Rectangle(E(18), E(12), Width - E(36), textHeight), wrap: false);
            int lineY = E(12) + textHeight + E(4);
            var line = new Rectangle(E(18), lineY, Math.Max(1, Width - E(36)), Math.Max(1, E(1)));
            using var fade = new LinearGradientBrush(line, Color.FromArgb(150, LauncherTheme.GoldBorder),
                                                     Color.FromArgb(0, LauncherTheme.GoldBorder), 0f);
            g.FillRectangle(fade, line);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _titleFont.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>A piece whose height depends on its width: wrapped text.</summary>
    internal interface IMeasured
    {
        int HeightFor(int width);
    }

    /// <summary>
    /// One setting: what it is on the left, with a line of explanation under it, and its control
    /// on the right; the two centred on each other.
    /// </summary>
    internal sealed class SettingRow : Control, IMeasured
    {
        private readonly float _scale;
        private readonly Control _input;
        private readonly Font _labelFont, _hintFont;
        private readonly string _hint;
        private int E(int px) => (int)Math.Round(px * _scale);

        public SettingRow(string label, string hint, Control input, float scale)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Text = label;
            _hint = hint;
            _scale = scale;
            _input = input;
            _labelFont = LauncherTheme.CreateFont(13.5f);
            _hintFont = LauncherTheme.CreateFont(11.5f);
            Controls.Add(input);
            input.EnabledChanged += (s, e) => Invalidate();
        }

        private int TextWidth(int width) => Math.Max(E(80), width - _input.Width - E(16));
        private int LabelHeight(int width) => SettingsPaint.HeightOf(Text, _labelFont, TextWidth(width));

        /// <summary>The name and its hint, one under the other.</summary>
        private int TextHeight(int width)
        {
            int text = LabelHeight(width);
            if (!string.IsNullOrEmpty(_hint)) text += E(3) + SettingsPaint.HeightOf(_hint, _hintFont, TextWidth(width));
            return text;
        }

        public int HeightFor(int width) => Math.Max(_input.Height, TextHeight(width));

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_input == null) return;
            _input.Location = new Point(Width - _input.Width, Math.Max(0, (Height - _input.Height) / 2));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            int width = TextWidth(Width);
            int label = LabelHeight(Width);
            int top = Math.Max(0, (Height - TextHeight(Width)) / 2);
            bool on = _input.Enabled;
            SettingsPaint.Text(g, Text, _labelFont, on ? LauncherTheme.CardText : LauncherTheme.DisabledFieldText,
                               new Rectangle(0, top, width, label));
            if (!string.IsNullOrEmpty(_hint))
            {
                int hint = SettingsPaint.HeightOf(_hint, _hintFont, width);
                SettingsPaint.Text(g, _hint, _hintFont, on ? LauncherTheme.MutedGold : LauncherTheme.DisabledFieldText,
                                   new Rectangle(0, top + label + E(3), width, hint));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _labelFont.Dispose();
                _hintFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>A loose line of explanation inside a card.</summary>
    internal sealed class SettingNote : Control, IMeasured
    {
        private readonly Font _font;

        public SettingNote(string text)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Text = text;
            _font = LauncherTheme.CreateFont(11.5f);
        }

        public int HeightFor(int width) => SettingsPaint.HeightOf(Text, _font, width);

        protected override void OnPaint(PaintEventArgs e)
            => SettingsPaint.Text(e.Graphics, Text, _font, Enabled ? LauncherTheme.MutedGold : LauncherTheme.DisabledFieldText,
                                  new Rectangle(0, 0, Width, Height));

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _font.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>An on/off switch: a pill whose knob slides right and turns gold when on.</summary>
    internal sealed class ToggleSwitch : Control
    {
        private readonly float _scale;
        private bool _checked, _hover;
        public event EventHandler CheckedChanged;

        public ToggleSwitch(bool value, float scale)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw
                     | ControlStyles.Selectable, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = true;
            _scale = scale;
            _checked = value;
            Size = new Size((int)Math.Round(52 * scale), (int)Math.Round(28 * scale));
        }

        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            Checked = !Checked;
        }

        protected override bool IsInputKey(Keys keyData) => keyData == Keys.Space || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode != Keys.Space) return;
            Checked = !Checked;
            e.Handled = true;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = (int)Math.Round(2 * _scale);
            var track = new Rectangle(pad, pad, Width - 1 - pad * 2, Height - 1 - pad * 2);
            using var path = SettingsPaint.Rounded(track, track.Height / 2);

            if (_checked)
            {
                using var fill = new LinearGradientBrush(track, Color.FromArgb(222, 178, 52), Color.FromArgb(150, 104, 24),
                                                         LinearGradientMode.Vertical);
                g.FillPath(fill, path);
            }
            else
            {
                using var fill = new SolidBrush(SettingsPaint.FieldFill);
                g.FillPath(fill, path);
            }
            if (Focused) SettingsPaint.FocusGlow(g, path, _scale);
            Color edge = _checked ? LauncherTheme.LightGold : _hover ? LauncherTheme.GoldBorder : LauncherTheme.BorderBrown;
            using (var border = new Pen(edge, Math.Max(1f, _scale)))
                g.DrawPath(border, path);

            int inset = (int)Math.Round(3 * _scale);
            int knob = track.Height - inset * 2;
            int x = _checked ? track.Right - inset - knob : track.Left + inset;
            var dot = new Rectangle(x, track.Top + inset, knob, knob);
            using (var shadow = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
                g.FillEllipse(shadow, new Rectangle(dot.X, dot.Y + Math.Max(1, inset / 2), dot.Width, dot.Height));
            using (var knobFill = new SolidBrush(_checked ? LauncherTheme.FieldText : LauncherTheme.MutedGold))
                g.FillEllipse(knobFill, dot);
        }
    }

    /// <summary>
    /// A number with − and + at its sides, in place of Windows' spin box: typed, clicked (held
    /// down it keeps counting), or with the arrows and the wheel while it has the focus.
    /// </summary>
    internal sealed class NumberStepper : Control
    {
        private readonly float _scale;
        private readonly TextBox _box;
        private readonly System.Windows.Forms.Timer _repeat = new System.Windows.Forms.Timer();
        private readonly decimal _min, _max, _step;
        private readonly int _decimals;
        private readonly string _suffix;
        private decimal _value;
        private int _hot, _pressed, _repeatDirection;
        private bool _focused;
        public event EventHandler ValueChanged;

        public NumberStepper(decimal value, decimal min, decimal max, decimal step, int decimals, string suffix, float scale)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            _scale = scale;
            _min = min;
            _max = max;
            _step = step;
            _decimals = decimals;
            _suffix = suffix ?? "";
            Size = new Size((int)Math.Round(132 * scale), (int)Math.Round(30 * scale));

            _box = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = SettingsPaint.FieldFill,
                ForeColor = LauncherTheme.FieldText,
                Font = LauncherTheme.CreateFont(13.5f, FontStyle.Bold),
                TextAlign = HorizontalAlignment.Center,
                AutoSize = false,
            };
            _box.GotFocus += (s, e) => { _focused = true; Invalidate(); BeginInvoke(new Action(_box.SelectAll)); };
            _box.LostFocus += (s, e) => { _focused = false; Commit(); Invalidate(); };
            _box.KeyDown += BoxKeyDown;
            _box.MouseWheel += (s, e) => { if (_focused) Nudge(e.Delta > 0 ? 1 : -1); };
            Controls.Add(_box);

            _repeat.Tick += (s, e) =>
            {
                _repeat.Interval = 60;
                Nudge(_repeatDirection);
            };
            Value = value;
        }

        public decimal Value
        {
            get => _value;
            set
            {
                decimal clean = Math.Round(Math.Clamp(value, _min, _max), _decimals);
                bool changed = clean != _value;
                _value = clean;
                _box.Text = Format(clean);
                if (changed) ValueChanged?.Invoke(this, EventArgs.Empty);
                Invalidate();
            }
        }

        private string Format(decimal value)
        {
            string number = value.ToString(_decimals > 0 ? "0." + new string('#', _decimals) : "0", CultureInfo.CurrentCulture);
            return _suffix.Length == 0 ? number : number + " " + _suffix;
        }

        /// <summary>What was typed, whatever the decimal mark; anything else puts the old value back.</summary>
        private void Commit()
        {
            // The unit is taken out only when there is one: Replace("") throws.
            string typed = _suffix.Length > 0 ? _box.Text.Replace(_suffix, "") : _box.Text;
            typed = typed.Replace(" ", "").Replace(',', '.').Trim();
            if (decimal.TryParse(typed, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed)) Value = parsed;
            else Value = _value;
        }

        private void Nudge(int direction)
        {
            if (!Enabled || direction == 0) return;
            Commit();
            Value = _value + direction * _step;
            if (_focused) _box.SelectAll();
        }

        private void BoxKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Up: Nudge(1); break;
                case Keys.Down: Nudge(-1); break;
                case Keys.Enter: Commit(); _box.SelectAll(); break;
                default: return;
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private int ButtonWidth => Height;
        private Rectangle Minus => new Rectangle(0, 0, ButtonWidth, Height - 1);
        private Rectangle Plus => new Rectangle(Width - 1 - ButtonWidth, 0, ButtonWidth, Height - 1);

        private int Hit(Point p) => Minus.Contains(p) ? -1 : Plus.Contains(p) ? 1 : 0;

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_box == null) return;     // sizing the control in its constructor lays it out before the box exists
            int h = _box.PreferredHeight;
            _box.SetBounds(ButtonWidth + 2, Math.Max(1, (Height - h) / 2), Math.Max(10, Width - ButtonWidth * 2 - 4), h);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            _box.ForeColor = Enabled ? LauncherTheme.FieldText : LauncherTheme.DisabledFieldText;
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hot = Enabled ? Hit(e.Location) : 0;
            Cursor = hot != 0 ? Cursors.Hand : Cursors.Default;
            if (hot == _hot) return;
            _hot = hot;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hot = 0;
            StopRepeat();
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Enabled) return;
            int direction = Hit(e.Location);
            if (direction == 0) { _box.Focus(); return; }
            _pressed = direction;
            _repeatDirection = direction;
            Nudge(direction);
            _repeat.Interval = 400;
            _repeat.Start();
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            StopRepeat();
        }

        private void StopRepeat()
        {
            _repeat.Stop();
            if (_pressed == 0) return;
            _pressed = 0;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var area = new Rectangle(0, 0, Width - 1, Height - 1);
            int radius = (int)Math.Round(5 * _scale);
            using var path = SettingsPaint.Rounded(area, radius);
            using (var fill = new SolidBrush(SettingsPaint.FieldFill))
                g.FillPath(fill, path);

            // The two buttons: a lighter brown, lit when hovered, darker while held.
            foreach (int side in new[] { -1, 1 })
            {
                var r = side < 0 ? Minus : Plus;
                Color tone = !Enabled ? Color.FromArgb(22, 13, 7)
                           : _pressed == side ? Color.FromArgb(40, 25, 12)
                           : _hot == side ? LauncherTheme.LightBrown
                           : Color.FromArgb(44, 27, 14);
                var state = g.Save();
                g.SetClip(path);
                using (var brush = new SolidBrush(tone)) g.FillRectangle(brush, r);
                g.Restore(state);

                Color ink = !Enabled ? LauncherTheme.DisabledFieldText : _hot == side ? Color.White : LauncherTheme.SoftGold;
                using var pen = new Pen(ink, Math.Max(1.5f, 2f * _scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                int arm = (int)Math.Round(5 * _scale);
                var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
                g.DrawLine(pen, c.X - arm, c.Y, c.X + arm, c.Y);
                if (side > 0) g.DrawLine(pen, c.X, c.Y - arm, c.X, c.Y + arm);
            }

            using (var divider = new Pen(LauncherTheme.BorderBrown, Math.Max(1f, _scale)))
            {
                g.DrawLine(divider, Minus.Right, 1, Minus.Right, Height - 2);
                g.DrawLine(divider, Plus.Left, 1, Plus.Left, Height - 2);
            }
            if (_focused) SettingsPaint.FocusGlow(g, path, _scale);
            Color edge = !Enabled ? LauncherTheme.DisabledFieldBorder : _focused ? LauncherTheme.LightGold : LauncherTheme.BorderBrown;
            using (var border = new Pen(edge, Math.Max(1f, _scale)))
                g.DrawPath(border, path);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _repeat.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// The random loot's rarity table, two columns of a coloured dot, the rarity and its chance,
    /// so it reads at a glance instead of in a sentence.
    /// </summary>
    internal sealed class RarityTable : Control, IMeasured
    {
        private readonly float _scale;
        private readonly IReadOnlyList<(string Name, string Chance, Color Tone)> _lines;
        private readonly Font _font, _bold;
        private int E(int px) => (int)Math.Round(px * _scale);

        public RarityTable(IReadOnlyList<(string Name, string Chance, Color Tone)> lines, float scale)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            _lines = lines;
            _scale = scale;
            _font = LauncherTheme.CreateFont(12f);
            _bold = LauncherTheme.CreateFont(12f, FontStyle.Bold);
        }

        private int LineHeight => TextRenderer.MeasureText("W", _font).Height + E(7);
        private int Rows => (_lines.Count + 1) / 2;

        public int HeightFor(int width) => Rows * LineHeight + E(12);

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var box = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = SettingsPaint.Rounded(box, E(5)))
            using (var fill = new SolidBrush(SettingsPaint.FieldFill))
            using (var border = new Pen(Color.FromArgb(70, LauncherTheme.BorderBrown), Math.Max(1f, _scale)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            // The first column as wide as its longest line needs, the second with the rest: the rare
            // ones there carry the longer names ("Perfect + AP/MP exo").
            int dot = E(8);
            int usable = Width - E(12) * 2 - E(18);
            int LineWidth(int i) => dot + E(8) + TextRenderer.MeasureText(_lines[i].Name, _font).Width + E(10)
                                    + TextRenderer.MeasureText(_lines[i].Chance, _bold).Width;
            int first = Enumerable.Range(0, Math.Min(Rows, _lines.Count)).Max(LineWidth);
            first = Math.Clamp(first, usable * 2 / 5, usable / 2);
            int textHeight = TextRenderer.MeasureText("W", _font).Height;
            for (int i = 0; i < _lines.Count; i++)
            {
                var (name, chance, tone) = _lines[i];
                int col = i / Rows, row = i % Rows;
                int x = E(12) + (col == 0 ? 0 : first + E(18));
                int column = col == 0 ? first : usable - first;
                int y = E(6) + row * LineHeight + E(3);
                Color dotTone = Enabled ? tone : LauncherTheme.DisabledFieldText;
                using (var brush = new SolidBrush(dotTone))
                    g.FillEllipse(brush, x, y + (textHeight - dot) / 2, dot, dot);
                int chanceWidth = TextRenderer.MeasureText(chance, _bold).Width;
                SettingsPaint.Text(g, name, _font, Enabled ? LauncherTheme.CardText : LauncherTheme.DisabledFieldText,
                                   new Rectangle(x + dot + E(8), y, Math.Max(1, column - dot - E(18) - chanceWidth), textHeight),
                                   wrap: false);
                SettingsPaint.Text(g, chance, _bold, Enabled ? dotTone : LauncherTheme.DisabledFieldText,
                                   new Rectangle(x, y, column, textHeight), wrap: false, right: true);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _font.Dispose();
                _bold.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
