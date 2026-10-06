using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// The log, painted by hand so that the drawing is seen behind it.
    ///
    /// Before it was a <see cref="RichTextBox"/>, and there the discussion ended: a WinForms
    /// text box is OPAQUE. There is no transparent colour that will do, no property to switch on; it paints
    /// its background and that is it. That is why the log was a black rectangle stuck on top of the background, with a
    /// frame around it so it did not look like a hole.
    ///
    /// This inherits from <see cref="LauncherPanel"/>, which already knows how to cut out the piece of background that belongs to it
    /// and paint colour layers on top. That is, the dark veil that makes the text readable is a
    /// layer with alpha, and underneath the drawing is still seen.
    ///
    /// ─── What is lost, and one has to know it ───────────────────────────────────────────────
    ///
    /// A text box allows selecting and copying with the mouse. This does not: they are drawn lines, not
    /// real text. In exchange for the transparency copy and paste is lost, so the same
    /// log is still written whole to the file in the logs folder, which is where it has
    /// to be taken from to paste it elsewhere.
    ///
    /// ─── Why it does not drag ───────────────────────────────────────────────────────────────
    ///
    /// Only the lines that fit on screen are drawn. It does not matter that there are four thousand stored: it
    /// works out which is the first visible and paints the thirty or forty that fit. With the
    /// monospaced font the line height is fixed, so knowing which it is is a division.
    /// </summary>
    public class LauncherLogView : LauncherPanel
    {
        /// <summary>A piece of line with its colour. A line is several.</summary>
        public readonly record struct Piece(string Text, Color Tone);

        private readonly List<Piece[]> _lines = new();

        /// <summary>Each line's plain text, to be able to see which one the mouse is over.</summary>
        private readonly List<string> _plain = new();
        private readonly VScrollBar _bar;
        private int _lineHeight = 14;
        private int _charWidth = 7;

        /// <summary>How many lines are kept before throwing the old ones away.</summary>
        public int Max { get; set; } = 4000;

        /// <summary>Whether the log stays stuck to the bottom as lines arrive.</summary>
        public bool Follow { get; set; } = true;

        public LauncherLogView()
        {
            DoubleBuffered = true;
            _bar = new VScrollBar { Dock = DockStyle.Right, Width = 16, Minimum = 0, Value = 0 };
            _bar.ValueChanged += (s, e) => Invalidate();
            Controls.Add(_bar);
        }

        /// <summary>How many lines fit at once.</summary>
        private int VisibleLineCount => Math.Max(1, (Height - Padding.Vertical) / _lineHeight);

        /// <summary>Adds a line already split by colours.</summary>
        public void Add(params Piece[] pieces)
        {
            _lines.Add(pieces);
            _plain.Add(string.Concat(pieces.Select(p => p.Text)));

            // It is thrown away in one go and not one by one: removing the first of a list of four thousand
            // moves the four thousand, and doing it on every line with the game running is noticeable.
            if (_lines.Count > Max + 512)
            {
                int extra = _lines.Count - Max;
                _lines.RemoveRange(0, extra);
                _plain.RemoveRange(0, extra);
            }

            Rescale();
            if (Follow) _bar.Value = _bar.Maximum;
            Invalidate();
        }

        public void Wipe()
        {
            _lines.Clear();
            _plain.Clear();
            Rescale();
            Invalidate();
        }

        /// <summary>Which line is under that point, or an empty string if there is none.</summary>
        public string TextAt(Point where)
        {
            int line = _bar.Value + (where.Y - Padding.Top) / Math.Max(1, _lineHeight);
            return line >= 0 && line < _plain.Count ? _plain[line] : "";
        }

        /// <summary>How many lines there are, for whoever wants to count them.</summary>
        public int Count => _lines.Count;

        private void Rescale()
        {
            int top = Math.Max(0, _lines.Count - VisibleLineCount);
            _bar.Maximum = top;
            _bar.LargeChange = 1;
            _bar.Enabled = top > 0;
            if (_bar.Value > top) _bar.Value = top;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Rescale();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            Measure();
            Rescale();
        }

        /// <summary>
        /// Line height and character width, measured once.
        ///
        /// The width is taken from a long strip and divided, instead of measuring a single character:
        /// measuring just one drags in the space the text engine leaves on the sides, and multiplied
        /// by a hundred characters the line goes several words out of place.
        /// </summary>
        private void Measure()
        {
            using var g = CreateGraphics();
            const string ruler = "0123456789012345678901234567890123456789";
            var size = TextRenderer.MeasureText(g, ruler, Font, new Size(int.MaxValue, int.MaxValue),
                                                TextFormatFlags.NoPadding);
            _charWidth = Math.Max(1, size.Width / ruler.Length);
            _lineHeight = Math.Max(1, size.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // The cropped background, the veil and the frame: LauncherPanel does all that.
            base.OnPaint(e);

            if (_charWidth <= 1) Measure();

            var g = e.Graphics;
            int first = _bar.Value;
            int x = Padding.Left;
            int y = Padding.Top;

            for (int i = first; i < _lines.Count && y + _lineHeight <= Height - Padding.Bottom; i++)
            {
                int left = x;
                foreach (var piece in _lines[i])
                {
                    if (piece.Text.Length == 0) continue;
                    TextRenderer.DrawText(g, piece.Text, Font, new Point(left, y), piece.Tone,
                                          TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    left += piece.Text.Length * _charWidth;
                }
                y += _lineHeight;
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!_bar.Enabled) return;

            int step = Math.Max(1, SystemInformation.MouseWheelScrollLines);
            int next = _bar.Value - Math.Sign(e.Delta) * step;
            _bar.Value = Math.Max(_bar.Minimum, Math.Min(_bar.Maximum, next));

            // Scrolling up releases the follow, and going back down hooks it again. It is what
            // any console does and what the hand expects without thinking.
            Follow = _bar.Value >= _bar.Maximum;
        }
    }
}
