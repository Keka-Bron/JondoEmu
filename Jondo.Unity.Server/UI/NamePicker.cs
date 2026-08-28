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
    /// Elegir el nombre de un opcode de entre los que el cliente lleva dentro.
    ///
    /// No es un campo de texto a propósito. Escribir a mano es lo que nos metió en el lío anterior:
    /// los 99 nombres que proponían las anclas los escribimos nosotros por analogía con Dofus 2 y
    /// ninguno era el de Ankama. Aquí sólo se puede elegir de la lista real, así que lo que salga es
    /// un nombre que existe de verdad; lo único que hay que acertar es cuál.
    ///
    /// El filtro va por trozos sueltos: escribir «map mov» encuentra
    /// <c>MapMovementConfirmResponse</c> sin tener que recordar el orden ni la mayúscula. Con 513
    /// nombres, buscar por prefijo sería inservible.
    /// </summary>
    internal sealed class NamePicker : Form, IBackgroundWindow
    {
        private readonly TextBox _filter;
        private readonly ListBox _list;
        private readonly float _escala;

        /// <summary>Las familias que sugiere el código del cliente para este opcode.</summary>
        private readonly HashSet<string> _hints;

        /// <summary>El nombre elegido, o cadena vacía si se ha soltado la ligadura.</summary>
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

            // El significado medido va delante de la lista: es lo que permite reconocer el mensaje.
            // Sin él esto sería elegir un nombre bonito de entre quinientos.
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
        /// Si el código del cliente apunta a este nombre.
        ///
        /// Vale por dos vías: que la FAMILIA coincida —el mensaje lo toca Core.UILogic.Inventory y
        /// el nombre vive en el dominio «inventory»— o que la pista aparezca dentro del propio
        /// nombre. Lo segundo pesca lo que el ofuscador dejó escapar en las máquinas de estado:
        /// «&lt;WaitProcessMapComplementaryInfo&gt;d__31» lleva dentro media respuesta.
        /// </summary>
        private bool Suggested(string name)
        {
            if (_hints.Count == 0) return false;
            if (_hints.Contains(NameBinding.Domain(name))) return true;

            string plain = name.ToLowerInvariant();
            return _hints.Any(h => h.Length >= 6 && plain.Contains(h, StringComparison.Ordinal));
        }

        /// <summary>Rellena la lista con lo que case con todos los trozos del filtro.</summary>
        private void Fill()
        {
            string[] parts = _filter.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // El orden tiene tres criterios, y en este orden:
            //
            //   1. la FAMILIA que sugiere el código del cliente. Si al mensaje lo toca
            //      Core.UILogic.Inventory, los nombres del dominio «inventory» van arriba. Es lo que
            //      convierte elegir entre 513 en confirmar entre una docena.
            //   2. los que EMPIEZAN por lo escrito. Buscar por dentro hace falta —«mov» tiene que
            //      encontrar MapMovementEvent— pero la mano escribe esperando un prefijo.
            //   3. alfabético, para que la lista no baile entre pulsaciones.
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
