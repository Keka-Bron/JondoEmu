using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// La cara del servidor.
    ///
    /// El registro se veía en el lanzador, que era el mismo proceso. Desde que son dos, el registro
    /// es del servidor y se ve aquí: quien lleva el servidor lo tiene delante sin depender de que
    /// haya un lanzador abierto, y el lanzador que se reparte a los jugadores se queda sin ninguna
    /// forma de leer la consola de nadie.
    ///
    /// Todo lo que se pinta sale de lo que el servidor ya sabe: no hay ninguna cuenta inventada
    /// para rellenar el panel. Lo que no se sepa medir, no se enseña.
    /// </summary>
    internal sealed class VentanaDelServidor : Form
    {
        // ─── Colores ────────────────────────────────────────────────────────────────────────
        //
        // Sacados del fondo para que la ventana no parezca dos cosas pegadas: el turquesa de los
        // cristales del Wakfu y el ámbar del atardecer.

        private static readonly Color Turquesa = Color.FromArgb(64, 224, 208);
        private static readonly Color Ambar = Color.FromArgb(255, 176, 74);
        private static readonly Color Tinta = Color.FromArgb(12, 18, 28);
        private static readonly Color Velo = Color.FromArgb(178, 10, 15, 24);
        private static readonly Color Texto = Color.FromArgb(226, 236, 246);
        private static readonly Color Apagado = Color.FromArgb(138, 152, 168);

        private Image? _fondo;
        private readonly Panel _lienzo;
        private readonly RichTextBox _registro;
        private readonly Panel _cifras;
        private readonly System.Windows.Forms.Timer _reloj;
        private readonly CheckBox _seguir;
        private readonly Button _parar;

        private long _ultimaLinea;
        private readonly DateTime _arranque = DateTime.UtcNow;
        private readonly Process _yo = Process.GetCurrentProcess();

        /// <summary>Cada cifra que se enseña, con su etiqueta y de dónde sale.</summary>
        private sealed class Cifra
        {
            public string Etiqueta = "";
            public Func<string> Valor = () => "";
            public Color Tono = Turquesa;
            public string Ultimo = "";
        }

        private readonly List<Cifra> _lista = new();

        /// <summary>Cuánto hay que multiplicar los píxeles: 1 al 100%, 2 al 200%.</summary>
        private readonly float _escala;

        private int E(int px) => (int)Math.Round(px * _escala);
        private Size Escalada(int ancho, int alto) => new Size(E(ancho), E(alto));
        private Font Letra(float cuerpo, FontStyle estilo = FontStyle.Regular, string familia = "Segoe UI")
            => new Font(familia, cuerpo * _escala, estilo);

        public VentanaDelServidor()
        {
            Text = "Jondo — Servidor";
            StartPosition = FormStartPosition.CenterScreen;

            // Todo lo que se dibuja a mano va multiplicado por esto.
            //
            // La primera versión salía descuadrada en una pantalla al 200%: las cajas de las cifras
            // se pisaban unas a otras y el título se metía encima. Los tamaños en píxeles y los
            // cuerpos de letra hay que escalarlos a mano, porque el dibujo es propio y WinForms no
            // escala lo que uno pinta en un Paint.
            _escala = DeviceDpi / 96f;

            MinimumSize = Escalada(980, 620);
            Size = Escalada(1180, 720);
            BackColor = Tinta;
            ForeColor = Texto;
            DoubleBuffered = true;
            TryCargarIcono();
            _fondo = CargarFondo();

            _lienzo = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            _lienzo.Paint += PintarFondo;
            Controls.Add(_lienzo);

            // ─── El orden importa ───────────────────────────────────────────────────────────
            //
            // WinForms acopla de delante hacia atrás, así que el panel que rellena (Fill) tiene que
            // ir DETRÁS de los que se pegan a un borde (Top, Bottom). Estaba al revés —el Fill
            // traído al frente— y se quedaba con toda la ventana: la fila de cifras existía, tenía
            // su manejador de Paint puesto, y no se dibujaba nunca porque le quedaban cero píxeles.
            // No daba ningún error; simplemente no estaba.
            var caja = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(E(18), E(4), E(18), E(16)) };
            _lienzo.Controls.Add(caja);

            _cifras = new Panel { Dock = DockStyle.Top, Height = E(152), BackColor = Color.Transparent };
            _cifras.Paint += PintarCifras;
            _lienzo.Controls.Add(_cifras);
            _cifras.BringToFront();

            DefinirCifras();

            var barra = new Panel { Dock = DockStyle.Bottom, Height = E(46), BackColor = Color.Transparent };
            caja.Controls.Add(barra);

            _registro = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(8, 12, 20),
                ForeColor = Texto,
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                Font = Letra(9f, FontStyle.Regular, "Consolas"),
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Both,
                DetectUrls = false,
            };
            caja.Controls.Add(_registro);
            _registro.SendToBack();   // el Fill, detrás de la barra de abajo

            _seguir = new CheckBox
            {
                Text = "Seguir el final",
                Checked = true,
                ForeColor = Apagado,
                BackColor = Color.Transparent,
                AutoSize = true,
                Location = new Point(E(2), E(14)),
            };
            barra.Controls.Add(_seguir);

            var limpiar = Boton("Limpiar", Apagado);
            limpiar.Click += (s, e) => _registro.Clear();
            barra.Controls.Add(limpiar);

            _parar = Boton("Detener el servidor", Color.FromArgb(232, 96, 96));
            _parar.Click += PararloTodo;
            barra.Controls.Add(_parar);

            // Los botones a la derecha, colocados a mano. Va en un método y se llama también al
            // final del constructor: enganchado sólo al Resize, la primera vez no se ejecutaba y
            // los botones salían encima de la casilla de la izquierda.
            void Colocar()
            {
                _parar.Location = new Point(barra.Width - _parar.Width - E(2), E(8));
                limpiar.Location = new Point(_parar.Left - limpiar.Width - E(10), E(8));
            }
            barra.Resize += (s, e) => Colocar();
            Colocar();

            // ─── El latido ──────────────────────────────────────────────────────────────────
            _reloj = new System.Windows.Forms.Timer { Interval = 1000 };
            _reloj.Tick += (s, e) => Refrescar();
            _reloj.Start();

            Refrescar();
        }

        // ─── Las cifras ─────────────────────────────────────────────────────────────────────

        private void DefinirCifras()
        {
            // Cada una sale de algo que el servidor sabe de verdad. Los jugadores conectados son
            // las sesiones vivas del puerto de juego, no los clientes lanzados: un cliente puede
            // estar abierto sin haber llegado a entrar.
            _lista.Add(new Cifra
            {
                Etiqueta = "JUGADORES",
                Tono = Turquesa,
                Valor = () => Network.GameNodeProxy.SesionesVivas.Count.ToString(),
            });
            _lista.Add(new Cifra
            {
                Etiqueta = "CLIENTES",
                Tono = Turquesa,
                Valor = () => $"{Network.ClientLaunchRegistry.ActiveCount}/{Network.ClientLaunchRegistry.MaximumClients}",
            });
            _lista.Add(new Cifra
            {
                Etiqueta = "COMBATES",
                Tono = Ambar,
                Valor = () => Handlers.FightHandler.CombatesEnCurso.ToString(),
            });
            _lista.Add(new Cifra
            {
                Etiqueta = "MAPAS",
                Tono = Ambar,
                Valor = () =>
                {
                    var mapas = new HashSet<long>();
                    foreach (var sesion in Network.GameNodeProxy.SesionesVivas.Values)
                    {
                        if (sesion.IsInWorld) mapas.Add(sesion.MapId);
                    }
                    return mapas.Count.ToString();
                },
            });
            _lista.Add(new Cifra
            {
                Etiqueta = "MEMORIA",
                Tono = Apagado,
                Valor = () => $"{GC.GetTotalMemory(false) / (1024 * 1024)} MB",
            });
            _lista.Add(new Cifra
            {
                Etiqueta = "TIEMPO",
                Tono = Apagado,
                Valor = () =>
                {
                    var va = DateTime.UtcNow - _arranque;
                    return va.TotalHours >= 1
                        ? $"{(int)va.TotalHours}h {va.Minutes:00}m"
                        : $"{va.Minutes}m {va.Seconds:00}s";
                },
            });
        }

        private bool _yaAvise;

        private void PintarCifras(object? sender, PaintEventArgs e)
        {
            // Un Paint que revienta no se ve: la fila se queda en blanco y no hay ni un mensaje.
            // Pasó, y costó más de lo que debería averiguar por qué.
            try { PintarCifrasDeVerdad(e.Graphics); }
            catch (Exception ex)
            {
                if (!_yaAvise)
                {
                    _yaAvise = true;
                    Console.WriteLine($"[Servidor] La fila de cifras no se ha podido pintar: {ex}");
                }
            }
        }

        private void PintarCifrasDeVerdad(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int margen = E(18);
            int hueco = E(12);
            int ancho = Math.Max(E(120), (_cifras.Width - margen * 2 - hueco * (_lista.Count - 1)) / _lista.Count);
            int alto = E(86);
            int y = E(46);

            // El título, y detrás la versión. Se mide en vez de suponer un ancho: con "JONDO" a
            // 13 puntos y la escala de la pantalla encima, el hueco fijo que había aquí hacía que
            // se pisaran las dos cadenas.
            using var titulo = Letra(13f, FontStyle.Bold);
            using var sub = Letra(8f);
            g.DrawString("JONDO", titulo, new SolidBrush(Ambar), margen, E(2));
            float tras = margen + g.MeasureString("JONDO", titulo).Width + E(8);
            g.DrawString("servidor  ·  " + Contrato.Version, sub, new SolidBrush(Apagado), tras, E(9));

            for (int i = 0; i < _lista.Count; i++)
            {
                var cifra = _lista[i];
                var caja = new Rectangle(margen + i * (ancho + hueco), y, ancho, alto);
                using var fondo = new SolidBrush(Color.FromArgb(170, 6, 10, 18));
                using var camino = Redondeado(caja, E(10));
                g.FillPath(fondo, camino);
                using var borde = new Pen(Color.FromArgb(60, cifra.Tono), 1f);
                g.DrawPath(borde, camino);

                // Cada texto dentro de su rectángulo, con puntos suspensivos si no cabe.
                //
                // El intento anterior era guardar y restaurar el Clip del Graphics, y se llevó por
                // delante el Paint entero: la fila de cifras dejó de dibujarse del todo. Con un
                // rectángulo y un StringFormat el recorte lo hace GDI+ y no hay estado que
                // restaurar.
                using var dentro = new StringFormat(StringFormatFlags.NoWrap)
                {
                    Trimming = StringTrimming.EllipsisCharacter,
                };

                using var fEtiqueta = Letra(7.5f, FontStyle.Bold);
                using var pincelEtiqueta = new SolidBrush(Apagado);
                // El alto del rectángulo, MEDIDO. Poniéndolo a ojo, GDI+ recortaba los glifos por
                // arriba y por abajo: se veía media letra.
                g.DrawString(cifra.Etiqueta, fEtiqueta, pincelEtiqueta,
                             new RectangleF(caja.X + E(11), caja.Y + E(10), caja.Width - E(18),
                                            fEtiqueta.GetHeight(g) + 2), dentro);

                using var fValor = Letra(15f, FontStyle.Bold);
                using var pincelValor = new SolidBrush(cifra.Tono);
                g.DrawString(cifra.Ultimo, fValor, pincelValor,
                             new RectangleF(caja.X + E(9), caja.Y + E(30), caja.Width - E(14),
                                            fValor.GetHeight(g) + 2), dentro);
            }
        }

        private static GraphicsPath Redondeado(Rectangle r, int radio)
        {
            var camino = new GraphicsPath();
            int d = radio * 2;
            camino.AddArc(r.X, r.Y, d, d, 180, 90);
            camino.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            camino.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            camino.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            camino.CloseFigure();
            return camino;
        }

        // ─── El fondo ───────────────────────────────────────────────────────────────────────

        private static Image? CargarFondo()
        {
            foreach (string nombre in new[] { "servidor_fondo.jpg", "servidor_fondo.png" })
            {
                try
                {
                    string ruta = Path.Combine(Paths.Root, "launcher_assets", nombre);
                    if (File.Exists(ruta)) return Image.FromFile(ruta);
                }
                catch { }
            }
            return null;   // sin imagen se pinta un degradado; la ventana no depende de ella
        }

        private void PintarFondo(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            var todo = _lienzo.ClientRectangle;
            if (todo.Width <= 0 || todo.Height <= 0) return;

            if (_fondo != null)
            {
                // Se cubre la ventana sin deformar la imagen: se escala por el lado que falte y se
                // recorta lo que sobre, como un fondo de escritorio.
                float escala = Math.Max((float)todo.Width / _fondo.Width, (float)todo.Height / _fondo.Height);
                int ancho = (int)(_fondo.Width * escala);
                int alto = (int)(_fondo.Height * escala);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(_fondo, (todo.Width - ancho) / 2, (todo.Height - alto) / 2, ancho, alto);
            }
            else
            {
                using var degradado = new LinearGradientBrush(todo, Color.FromArgb(18, 30, 46), Tinta, 60f);
                g.FillRectangle(degradado, todo);
            }

            // Un velo encima, porque encima va texto y sobre un atardecer no se lee.
            using var velo = new SolidBrush(Velo);
            g.FillRectangle(velo, todo);
        }

        // ─── El latido ──────────────────────────────────────────────────────────────────────

        private void Refrescar()
        {
            bool cambio = false;
            foreach (var cifra in _lista)
            {
                string ahora;
                try { ahora = cifra.Valor(); }
                catch { ahora = "—"; }
                if (ahora != cifra.Ultimo) { cifra.Ultimo = ahora; cambio = true; }
            }
            if (cambio) _cifras.Invalidate();

            TraerRegistro();
        }

        /// <summary>
        /// Las líneas nuevas de la consola.
        ///
        /// Salen del mismo buffer que ya intercepta Console.Out, así que se ve exactamente lo que
        /// escribe el servidor, sin tocar ni una línea de lo que ya escribía.
        /// </summary>
        private void TraerRegistro()
        {
            string json;
            try { json = ConsoleLogBuffer.GetLogsJson(_ultimaLinea); }
            catch { return; }

            List<(long Id, string Hora, string Texto)> nuevas = new();
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("logs", out var lista)) return;
                foreach (var linea in lista.EnumerateArray())
                {
                    nuevas.Add((
                        linea.TryGetProperty("id", out var id) ? id.GetInt64() : 0,
                        linea.TryGetProperty("time", out var h) ? (h.GetString() ?? "") : "",
                        linea.TryGetProperty("msg", out var m) ? (m.GetString() ?? "") : ""));
                }
            }
            catch { return; }

            if (nuevas.Count == 0) return;

            foreach (var (id, hora, texto) in nuevas)
            {
                if (id > _ultimaLinea) _ultimaLinea = id;
                Escribir(hora, texto);
            }

            // Que no crezca sin fin: una ventana abierta una semana no puede guardar la semana.
            if (_registro.Lines.Length > 4000)
            {
                _registro.Select(0, _registro.GetFirstCharIndexFromLine(1500));
                _registro.SelectedText = "";
            }

            if (_seguir.Checked)
            {
                _registro.SelectionStart = _registro.TextLength;
                _registro.ScrollToCaret();
            }
        }

        private void Escribir(string hora, string texto)
        {
            _registro.SelectionStart = _registro.TextLength;
            _registro.SelectionLength = 0;

            _registro.SelectionColor = Color.FromArgb(96, 110, 128);
            _registro.AppendText(hora.Length > 0 ? hora + "  " : "");

            _registro.SelectionColor = ColorDe(texto);
            _registro.AppendText(texto + "\n");
        }

        /// <summary>
        /// De qué color va cada línea. Se mira lo que ya escribe el servidor, sin inventarse
        /// ningún formato nuevo: los prefijos entre corchetes que lleva usando siempre.
        /// </summary>
        private static Color ColorDe(string linea)
        {
            if (linea.Contains("[!]") || linea.Contains("Error") || linea.Contains("error"))
                return Color.FromArgb(240, 120, 120);
            if (linea.Contains("Rechazad") || linea.Contains("rechazad"))
                return Color.FromArgb(240, 180, 110);
            if (linea.Contains("[Combate]") || linea.Contains("[FightHandler]"))
                return Ambar;
            if (linea.Contains("[Control]") || linea.Contains("[Comandos]"))
                return Turquesa;
            if (linea.Contains("[Game Node]") || linea.Contains("[Connection Server]"))
                return Color.FromArgb(150, 200, 230);
            if (linea.Contains("[DatabaseManager]") || linea.Contains("[World]") || linea.Contains("[+]"))
                return Color.FromArgb(150, 170, 190);
            return Texto;
        }

        // ─── Botones ────────────────────────────────────────────────────────────────────────

        private Button Boton(string texto, Color tono)
        {
            var letra = Letra(9f, FontStyle.Bold);
            var boton = new Button
            {
                Text = texto,
                AutoSize = false,
                Size = new Size(TextRenderer.MeasureText(texto, letra).Width + E(30), E(30)),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(16, 22, 32),
                ForeColor = tono,
                Font = letra,
                Cursor = Cursors.Hand,
            };
            boton.FlatAppearance.BorderColor = Color.FromArgb(70, tono);
            boton.FlatAppearance.MouseOverBackColor = Color.FromArgb(28, 38, 52);
            return boton;
        }

        private void PararloTodo(object? sender, EventArgs e)
        {
            var seguro = MessageBox.Show(
                "Se va a parar el servidor. Los jugadores que estén dentro perderán la conexión.\n\n¿Seguro?",
                "Jondo — Servidor", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (seguro != DialogResult.Yes) return;

            _parar.Enabled = false;
            _parar.Text = "Parando...";
            Program.RequestShutdown("botón de la ventana del servidor");
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Cerrar la ventana del servidor SÍ para el servidor: es su ventana, no la de otro.
            // Pero se pregunta, porque puede haber gente jugando y una X es fácil de dar sin querer.
            if (e.CloseReason == CloseReason.UserClosing && !Program.ApagandoYa)
            {
                int dentro = Network.GameNodeProxy.SesionesVivas.Count;
                string aviso = dentro > 0
                    ? $"Hay {dentro} jugador(es) conectado(s) y perderán la conexión.\n\n¿Parar el servidor?"
                    : "¿Parar el servidor?";
                if (MessageBox.Show(aviso, "Jondo — Servidor", MessageBoxButtons.YesNo,
                                    MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                Program.RequestShutdown("ventana del servidor cerrada");
            }

            _reloj.Stop();
            base.OnFormClosing(e);
        }

        private void TryCargarIcono()
        {
            try
            {
                string ruta = Path.Combine(Paths.Root, "launcher_assets", "favicon.ico");
                if (File.Exists(ruta)) Icon = new Icon(ruta);
            }
            catch { }
        }

        /// <summary>Abre la ventana en su propio hilo, para que no estorbe a los servicios.</summary>
        public static VentanaDelServidor? Abrir()
        {
            VentanaDelServidor? ventana = null;
            var lista = new System.Threading.ManualResetEventSlim(false);

            var hilo = new System.Threading.Thread(() =>
            {
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    try { Application.SetHighDpiMode(HighDpiMode.SystemAware); } catch { }
                    ventana = new VentanaDelServidor();
                    lista.Set();
                    Application.Run(ventana);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Servidor] No se ha podido abrir la ventana: {ex.Message}");
                    lista.Set();
                }
            })
            {
                Name = "VentanaDelServidor",
                IsBackground = true,
            };
            hilo.SetApartmentState(System.Threading.ApartmentState.STA);
            hilo.Start();
            lista.Wait(TimeSpan.FromSeconds(10));
            return ventana;
        }
    }
}
