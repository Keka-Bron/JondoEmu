using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>A small launcher-styled text prompt used for editable connection settings.</summary>
    internal sealed class LauncherInputDialog : Form
    {
        private readonly TextBox _value;

        private LauncherInputDialog(string title, string message, string initialValue,
                                    string accept, string cancel)
        {
            float zoom = LauncherTheme.UiZoom;
            int P(float value) => (int)Math.Round(value * zoom);

            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = LauncherTheme.Background;
            ForeColor = LauncherTheme.BaseText;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(P(520), P(226));

            var heading = new Label
            {
                Text = title,
                ForeColor = LauncherTheme.LightGold,
                BackColor = Color.Transparent,
                Font = LauncherTheme.CreateFont(16f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Bounds = new Rectangle(P(24), P(16), P(472), P(32)),
            };

            var explanation = new Label
            {
                Text = message,
                ForeColor = LauncherTheme.BaseText,
                BackColor = Color.Transparent,
                Font = LauncherTheme.CreateFont(10.5f),
                TextAlign = ContentAlignment.MiddleLeft,
                Bounds = new Rectangle(P(24), P(54), P(472), P(44)),
            };

            _value = new TextBox
            {
                Text = initialValue,
                BackColor = LauncherTheme.FieldBackground,
                ForeColor = LauncherTheme.FieldText,
                BorderStyle = BorderStyle.FixedSingle,
                Font = LauncherTheme.CreateFont(12f),
                Bounds = new Rectangle(P(24), P(108), P(472), P(32)),
            };

            var ok = ActionButton(accept, true, P);
            ok.Bounds = new Rectangle(P(252), P(160), P(116), P(42));
            ok.DialogResult = DialogResult.OK;

            var no = ActionButton(cancel, false, P);
            no.Bounds = new Rectangle(P(380), P(160), P(116), P(42));
            no.DialogResult = DialogResult.Cancel;

            AcceptButton = ok;
            CancelButton = no;
            Controls.Add(heading);
            Controls.Add(explanation);
            Controls.Add(_value);
            Controls.Add(ok);
            Controls.Add(no);

            Shown += (s, e) =>
            {
                _value.Focus();
                _value.SelectAll();
            };
        }

        private static Button ActionButton(string text, bool primary, Func<float, int> P)
        {
            var button = new Button
            {
                Text = text,
                Font = LauncherTheme.CreateFont(10.5f, FontStyle.Bold),
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? LauncherTheme.GreenTop : LauncherTheme.CardFill,
                ForeColor = primary ? Color.White : LauncherTheme.SoftGold,
                Cursor = Cursors.Hand,
            };
            button.FlatAppearance.BorderColor = primary
                ? LauncherTheme.GreenBorder
                : LauncherTheme.BorderBrown;
            button.FlatAppearance.BorderSize = Math.Max(1, P(1));
            return button;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var line = new Pen(Color.FromArgb(120, LauncherTheme.BorderBrown));
            e.Graphics.DrawLine(line, 24, 52, ClientSize.Width - 24, 52);
        }

        public static bool Prompt(IWin32Window owner, string title, string message,
                                  string initialValue, string accept, string cancel,
                                  out string value)
        {
            using var dialog = new LauncherInputDialog(title, message, initialValue, accept, cancel);
            bool accepted = dialog.ShowDialog(owner) == DialogResult.OK;
            value = dialog._value.Text.Trim();
            return accepted;
        }
    }
}
