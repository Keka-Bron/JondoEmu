using Jondo.Unity.Launcher.UI;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.UI
{
    /// <summary>
    /// Choosing an opcode's name from among those the client carries inside.
    ///
    /// It is not a text field on purpose. Typing by hand is what got us into the previous mess:
    /// the 99 names the anchors proposed were written by us by analogy with Dofus 2 and
    /// none was Ankama's. Here one can only choose from the real list, so whatever comes out is
    /// a name that really exists; the only thing to get right is which one.
    ///
    /// The filter goes by loose pieces: typing «map mov» finds
    /// <c>MapMovementConfirmResponse</c> without having to remember the order or the capitals. With 513
    /// names, searching by prefix would be useless.
    /// </summary>
    internal sealed class NamePicker : Form, IBackgroundWindow
    {
        private readonly TextBox _filter;
        private readonly ListBox _list;
        private readonly float _escala;

        /// <summary>The families the client code suggests for this opcode.</summary>
        private readonly HashSet<string> _hints;

        /// <summary>The chosen name, or an empty string if the binding has been released.</summary>
        public string Chosen { get; private set; } = "";

        public Image? ComposedBackground => null;

        private int E(int px) => (int)Math.Round(px * _escala);

        public NamePicker(string opcode, string meaning, string current, float escala)
        {
            _escala = escala;
            _hints = new HashSet<string>(NameBinding.Hints(opcode), StringComparer.Ordinal);

            Text = $"Qué es «{opcode}»";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(E(620), E(560));
            BackColor = LauncherTheme.Background;
            ForeColor = LauncherTheme.BaseText;

            var titulo = new Label
            {
                Text = opcode + (current.Length > 0 ? "  —  ahora: " + current : "  —  sin ligar"),
                Bounds = new Rectangle(E(16), E(12), E(588), E(22)),
                ForeColor = LauncherTheme.LightGold,
                Font = LauncherTheme.CreateFont(15f, FontStyle.Bold),
                BackColor = Color.Transparent,
            };

            // The measured meaning goes before the list: it is what allows recognising the message.
            // Without it this would be choosing a pretty name from among five hundred.
            var contexto = new Label
            {
                Text = (meaning.Length > 0 ? meaning : "(no hay significado medido para este opcode)") +
                       (_hints.Count > 0
                            ? Environment.NewLine + "Lo toca código del cliente en: " +
                              string.Join(", ", _hints.Take(6))
                            : ""),
                Bounds = new Rectangle(E(16), E(38), E(588), E(52)),
                AutoSize = false,
                ForeColor = LauncherTheme.LightBrownText,
                Font = LauncherTheme.CreateFont(12f),
                BackColor = Color.Transparent,
            };

            _filter = new TextBox
            {
                Bounds = new Rectangle(E(16), E(96), E(588), E(28)),
                BackColor = Color.FromArgb(13, 7, 4),
                ForeColor = LauncherTheme.FieldText,
                BorderStyle = BorderStyle.FixedSingle,
                Font = LauncherTheme.CreateMonoFont(14f),
            };
            _filter.TextChanged += (s, e) => Fill();

            _list = new ListBox
            {
                Bounds = new Rectangle(E(16), E(132), E(588), E(368)),
                BackColor = Color.FromArgb(13, 7, 4),
                ForeColor = LauncherTheme.BaseText,
                BorderStyle = BorderStyle.FixedSingle,
                Font = LauncherTheme.CreateMonoFont(13f),
            };
            _list.DoubleClick += (s, e) => Accept();

            var ligar = Boton("LIGAR", LauncherTheme.GreenTop, E(16));
            ligar.Click += (s, e) => Accept();

            var soltar = Boton("SOLTAR", LauncherTheme.MutedGold, E(210));
            soltar.Click += (s, e) => { Chosen = ""; DialogResult = DialogResult.OK; Close(); };

            var cerrar = Boton("CANCELAR", LauncherTheme.LightBrownText, E(404));
            cerrar.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            Controls.AddRange(new Control[] { titulo, contexto, _filter, _list, ligar, soltar, cerrar });
            Fill();
            _filter.Select();
        }

        private LauncherButton Boton(string texto, Color tono, int x) => new()
        {
            Text = texto,
            Bounds = new Rectangle(x, E(512), E(188), E(34)),
            Font = LauncherTheme.CreateFont(13f, FontStyle.Bold),
            CornerRadius = E(5),
            BackgroundTop = Color.FromArgb(200, 34, 21, 12),
            BackgroundBottom = Color.FromArgb(200, 22, 13, 8),
            BackgroundTopHighlight = LauncherTheme.LightBrown,
            BackgroundBottomHighlight = Color.FromArgb(130, 75, 35),
            BorderColor = LauncherTheme.BorderBrown,
            BorderColorHighlight = LauncherTheme.GoldBorder,
            TextColor = tono,
            TextColorHighlight = Color.White,
            Cursor = Cursors.Hand,
        };

        /// <summary>
        /// Whether the client code points to this name.
        ///
        /// It holds in two ways: the FAMILY matches —Core.UILogic.Inventory touches the message and
        /// the name lives in the «inventory» domain— or the hint appears inside the name
        /// itself. The second catches what the obfuscator let slip in the state machines:
        /// «&lt;WaitProcessMapComplementaryInfo&gt;d__31» carries half an answer inside.
        /// </summary>
        private bool Suggested(string name)
        {
            if (_hints.Count == 0) return false;
            if (_hints.Contains(NameBinding.Domain(name))) return true;

            string plain = name.ToLowerInvariant();
            return _hints.Any(h => h.Length >= 6 && plain.Contains(h, StringComparison.Ordinal));
        }

        /// <summary>Fills the list with whatever matches all the filter's pieces.</summary>
        private void Fill()
        {
            string[] parts = _filter.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // The order has three criteria, in this order:
            //
            //   1. the FAMILY the client code suggests. If
            //      Core.UILogic.Inventory touches the message, the names of the «inventory» domain go on top. It is what
            //      turns choosing among 513 into confirming among a dozen.
            //   2. the ones STARTING with what was typed. Searching inside is needed —«mov» has to
            //      find MapMovementEvent— but the hand types expecting a prefix.
            //   3. alphabetical, so that the list does not jump around between keystrokes.
            var matches = NameBinding.Catalogue()
                .Where(n => parts.All(p => n.Contains(p, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(n => Suggested(n) ? 0 : 1)
                .ThenBy(n => parts.Length > 0 &&
                             n.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Take(400)
                .ToArray();

            _list.BeginUpdate();
            _list.Items.Clear();
            _list.Items.AddRange(matches);
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            _list.EndUpdate();
        }

        private void Accept()
        {
            if (_list.SelectedItem == null) return;
            Chosen = _list.SelectedItem.ToString() ?? "";
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
