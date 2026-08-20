using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// Text box styled like .form-input: dark background, rounded brown border and a golden glow
    /// when it takes focus.
    ///
    /// The native box is embedded inside the control, which is the one that draws the frame. When
    /// the services are down the field is made read-only instead of disabled, because a disabled
    /// WinForms text box is painted with the gray system colors and would break the dark theme.
    /// </summary>
    public sealed class LauncherField : Control
    {
        /// <summary>Solid color equivalent to rgba(12, 6, 3, 0.85) over the card.</summary>
        private static readonly Color NormalBackground = Color.FromArgb(13, 7, 4);

        /// <summary>Solid color equivalent to rgba(20, 10, 5, 0.5) over the card.</summary>
        private static readonly Color LockedBackground = Color.FromArgb(19, 11, 6);

        private readonly TextBox _box;
        private bool _focused;
        private bool _locked;

        /// <summary>Raised when Enter is pressed inside the field.</summary>
        public event EventHandler? SubmitRequested;

        public LauncherField()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;

            _box = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = NormalBackground,
                ForeColor = LauncherTheme.FieldText,
                Font = LauncherTheme.CreateFont(13f),
                AutoSize = false
            };
            _box.GotFocus += (s, e) => { _focused = true; Invalidate(); };
            _box.LostFocus += (s, e) => { _focused = false; Invalidate(); };
            _box.KeyDown += BoxKeyDown;
            Controls.Add(_box);
        }

        /// <summary>Text typed by the user.</summary>
        public string Value
        {
            get => _box.Text;
            set => _box.Text = value;
        }

        /// <summary>Hint text shown while the field is empty.</summary>
        public string Placeholder
        {
            get => _box.PlaceholderText;
            set => _box.PlaceholderText = value;
        }

        /// <summary>Masks what is typed with dots.</summary>
        public bool IsPassword
        {
            get => _box.PasswordChar != '\0';
            set => _box.PasswordChar = value ? '•' : '\0';
        }

        /// <summary>Blocks typing and dims the colors, like the disabled attribute on the web.</summary>
        public bool Locked
        {
            get => _locked;
            set
            {
                if (_locked == value) return;
                _locked = value;
                _box.ReadOnly = value;
                _box.TabStop = !value;
                _box.BackColor = value ? LockedBackground : NormalBackground;
                _box.ForeColor = value ? LauncherTheme.DisabledFieldText : LauncherTheme.FieldText;
                _box.Cursor = value ? Cursors.No : Cursors.IBeam;
                Invalidate();
            }
        }

        /// <summary>Moves the keyboard focus to the inner box.</summary>
        public void FocusInput() => _box.Focus();

        private void BoxKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (!_locked) SubmitRequested?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            _box.Font = Font;
            LayOutBox();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            LayOutBox();
        }

        private void LayOutBox()
        {
            // Margins matching the padding: 10px 12px of the stylesheet.
            int margin = Math.Max(6, (int)Math.Round(Width * 0.035f));
            int height = _box.PreferredHeight;
            _box.SetBounds(margin, Math.Max(2, (Height - height) / 2), Math.Max(10, Width - margin * 2), height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color fill = _locked ? LockedBackground : NormalBackground;
            Color border = _locked ? LauncherTheme.DisabledFieldBorder
                         : _focused ? LauncherTheme.LightGold
                         : LauncherTheme.BorderBrown;

            var area = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = LauncherTheme.RoundedPath(area, 5))
            using (var brush = new SolidBrush(fill))
            {
                g.FillPath(brush, path);

                if (_focused && !_locked)
                {
                    // Golden focus glow (box-shadow 0 0 8px rgba(255,204,0,0.5)).
                    using var halo = new Pen(Color.FromArgb(90, 255, 204, 0), 3);
                    g.DrawPath(halo, path);
                }

                using var pen = new Pen(border, 1);
                g.DrawPath(pen, path);
            }
        }
    }
}
