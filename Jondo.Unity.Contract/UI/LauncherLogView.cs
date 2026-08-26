using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// El registro, pintado a mano para que se vea el dibujo por detrás.
    ///
    /// Antes era un <see cref="RichTextBox"/>, y ahí se acababa la discusión: un cuadro de texto de
    /// WinForms es OPACO. No hay color transparente que valga, no hay propiedad que activar; pinta
    /// su fondo y punto. Por eso el registro era un rectángulo negro pegado encima del fondo, con un
    /// marco alrededor para que no pareciera un agujero.
    ///
    /// Esto hereda de <see cref="LauncherPanel"/>, que ya sabe recortar el trozo de fondo que le toca
    /// y pintarle capas de color encima. O sea que el velo oscuro que hace legible el texto es una
    /// capa con alfa, y por debajo se sigue viendo el dibujo.
    ///
    /// ─── Lo que se pierde, y hay que saberlo ────────────────────────────────────────────────
    ///
    /// Un cuadro de texto deja seleccionar y copiar con el ratón. Esto no: son líneas dibujadas, no
    /// texto de verdad. A cambio de la transparencia se pierde el copiar y pegar, así que el mismo
    /// registro se sigue escribiendo entero en el fichero de la carpeta logs, que es de donde hay
    /// que sacarlo para pegarlo en otro sitio.
    ///
    /// ─── Por qué no arrastra ────────────────────────────────────────────────────────────────
    ///
    /// Sólo se dibujan las líneas que caben en pantalla. Da igual que haya cuatro mil guardadas: se
    /// calcula cuál es la primera visible y se pintan las treinta o cuarenta que entran. Con la
    /// letra monoespaciada la altura de línea es fija, así que saber cuál toca es una división.
    /// </summary>
    public class LauncherLogView : LauncherPanel
    {
        /// <summary>Un trozo de línea con su color. Una línea son varios.</summary>
        public readonly record struct Piece(string Text, Color Tone);

        private readonly List<Piece[]> _lines = new();

        /// <summary>El texto llano de cada renglón, para poder mirar sobre cuál está el ratón.</summary>
        private readonly List<string> _plain = new();
        private readonly VScrollBar _bar;
        private int _lineHeight = 14;
        private int _charWidth = 7;

        /// <summary>Cuántas líneas se guardan antes de tirar las viejas.</summary>
        public int Max { get; set; } = 4000;

        /// <summary>Si el registro se mantiene pegado abajo según llegan líneas.</summary>
        public bool Follow { get; set; } = true;

        public LauncherLogView()
        {
            DoubleBuffered = true;
            _bar = new VScrollBar { Dock = DockStyle.Right, Width = 16, Minimum = 0, Value = 0 };
            _bar.ValueChanged += (s, e) => Invalidate();
            Controls.Add(_bar);
        }

        /// <summary>Cuántas líneas caben de una vez.</summary>
        private int VisibleLineCount => Math.Max(1, (Height - Padding.Vertical) / _lineHeight);

        /// <summary>Añade una línea ya troceada por colores.</summary>
        public void Add(params Piece[] pieces)
        {
            _lines.Add(pieces);
            _plain.Add(string.Concat(pieces.Select(p => p.Text)));

            // Se tira de golpe y no de una en una: quitar el primero de una lista de cuatro mil
            // mueve los cuatro mil, y hacerlo en cada línea con el juego andando se nota.
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

        /// <summary>Qué renglón hay bajo ese punto, o cadena vacía si no hay ninguno.</summary>
        public string TextAt(Point where)
        {
            int line = _bar.Value + (where.Y - Padding.Top) / Math.Max(1, _lineHeight);
            return line >= 0 && line < _plain.Count ? _plain[line] : "";
        }

        /// <summary>Cuántas líneas hay, para quien quiera contarlas.</summary>
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
        /// Alto de línea y ancho de carácter, medidos una vez.
        ///
        /// El ancho se saca de una tira larga y se divide, en vez de medir un carácter suelto:
        /// medir uno solo arrastra el espacio que el motor de texto deja a los lados, y multiplicado
        /// por cien caracteres el renglón se va varias palabras de sitio.
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
            // El fondo recortado, el velo y el marco: todo eso lo hace LauncherPanel.
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

            // Rodar hacia arriba suelta el seguimiento, y volver abajo lo engancha otra vez. Es lo
            // que hace cualquier consola y lo que la mano espera sin pensarlo.
            Follow = _bar.Value >= _bar.Maximum;
        }
    }
}
