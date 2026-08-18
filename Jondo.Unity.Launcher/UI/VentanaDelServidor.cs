using System;
using System.Collections.Generic;
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
    /// es del servidor y se ve aquí: quien lo lleva lo tiene delante sin depender de que haya un
    /// lanzador abierto, y el lanzador que se reparte a los jugadores se queda sin ninguna forma de
    /// leerle la consola a nadie.
    ///
    /// Pinta con <see cref="LauncherTheme"/> y con <see cref="LauncherLogo"/>, que viven en el
    /// contrato y comparten los dos ejecutables: se parecen porque pintan con lo MISMO, no porque
    /// alguien haya copiado los colores de un sitio a otro.
    ///
    /// Todo lo que se enseña sale de lo que el servidor ya sabe. Nada inventado para rellenar.
    /// </summary>
    internal sealed class VentanaDelServidor : Form, IVentanaConFondo
    {
        private Image? _foto;
        private Bitmap? _fondoCompuesto;

        /// <summary>El fondo ya compuesto, para que los paneles se recorten su trozo.</summary>
        public Image? ComposedBackground => _fondoCompuesto;

        private readonly PanelSinParpadeo _cifras;
        private readonly RichTextBox _registro;
        private readonly LauncherLogo _logo;
        private readonly System.Windows.Forms.Timer _reloj;
        private readonly CheckBox _seguir;
        private readonly Button _parar;
        private readonly Button _limpiar;
        private readonly List<Button> _idiomas = new();

        private long _ultimaLinea;
        private readonly DateTime _arranque = DateTime.UtcNow;

        private Language _idioma = PreferenciasDelServidor.Language;
        private LauncherTexts _textos = LauncherTexts.Get(PreferenciasDelServidor.Language);

        private readonly float _escala;
        private int E(int px) => (int)Math.Round(px * _escala);
        private Font Letra(float cuerpo, FontStyle estilo = FontStyle.Regular)
            => new Font(LauncherTheme.TitleFamily, cuerpo * _escala, estilo);
        private Font Mono(float cuerpo) => new Font(LauncherTheme.MonoFamily, cuerpo * _escala);

        /// <summary>
        /// Un panel que no parpadea al repintarse.
        ///
        /// Las cifras se refrescan cada segundo y daban un pestañeo en cada una: un Panel normal
        /// borra el fondo y luego dibuja, y entre las dos cosas se ve el hueco. Con doble búfer se
        /// compone fuera de pantalla y se vuelca de una vez.
        /// </summary>
        private sealed class PanelSinParpadeo : Panel
        {
            public PanelSinParpadeo()
            {
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                UpdateStyles();
            }
        }

        /// <summary>Cada cifra: su etiqueta, de dónde sale y de qué color va.</summary>
        private sealed class Cifra
        {
            public Func<LauncherTexts, string> Etiqueta = _ => "";
            public Func<string> Valor = () => "";
            public Color Tono = LauncherTheme.LightGold;
            public string Ultimo = "";
        }

        private readonly List<Cifra> _lista = new();

        public VentanaDelServidor()
        {
            _escala = DeviceDpi / 96f;

            Text = "Jondo Server";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(E(900), E(560));
            Size = new Size(E(1280), E(760));
            // Maximizada, que es como se quiere ver un servidor: de un vistazo y sin colocarla.
            WindowState = FormWindowState.Maximized;
            BackColor = LauncherTheme.Background;
            ForeColor = LauncherTheme.BaseText;
            DoubleBuffered = true;
            TryCargarIcono();
            _foto = LauncherTheme.LoadImage("servidor_fondo.jpg") ?? LauncherTheme.LoadImage("bg.jpg");

            _logo = new LauncherLogo
            {
                Primera = "JONDO",
                Segunda = "SERVER",
                BackColor = Color.Transparent,
                Height = E(92),
                Dock = DockStyle.Top,
            };

            _cifras = new PanelSinParpadeo
            {
                Dock = DockStyle.Top,
                Height = E(102),
                BackColor = Color.Transparent,
            };
            _cifras.Paint += PintarCifras;
            DefinirCifras();

            var caja = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                Padding = new Padding(E(22), E(4), E(22), E(14)),
            };

            var barra = new Panel { Dock = DockStyle.Bottom, Height = E(44), BackColor = Color.Transparent };

            _registro = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = LauncherTheme.ConsoleBackground,
                ForeColor = LauncherTheme.LogNormal,
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                Font = Mono(9.5f),
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Both,
                DetectUrls = false,
            };

            caja.Controls.Add(_registro);
            caja.Controls.Add(barra);
            _registro.SendToBack();

            // El orden importa: WinForms acopla de delante hacia atrás, así que el que rellena va
            // DETRÁS de los que se pegan a un borde. Al revés, el Fill se queda con toda la ventana
            // y a los demás les tocan cero píxeles, sin dar ningún error.
            Controls.Add(caja);
            Controls.Add(_cifras);
            Controls.Add(_logo);

            _seguir = new CheckBox
            {
                Checked = true,
                ForeColor = LauncherTheme.MutedGold,
                BackColor = Color.Transparent,
                AutoSize = true,
                Font = Letra(8.5f),
                Location = new Point(E(2), E(13)),
            };
            barra.Controls.Add(_seguir);

            foreach (var cual in new[] { Language.Es, Language.En, Language.Fr })
            {
                var boton = Boton(LauncherTexts.Code(cual).ToUpperInvariant(), LauncherTheme.MutedGold, E(46));
                var elegido = cual;
                boton.Click += (s, e) => CambiarIdioma(elegido);
                _idiomas.Add(boton);
                barra.Controls.Add(boton);
            }

            _limpiar = Boton("", LauncherTheme.SoftGold);
            _limpiar.Click += (s, e) => _registro.Clear();
            barra.Controls.Add(_limpiar);

            _parar = Boton("", LauncherTheme.Red);
            _parar.Click += PararloTodo;
            barra.Controls.Add(_parar);

            void Colocar()
            {
                _parar.Location = new Point(barra.Width - _parar.Width - E(2), E(7));
                _limpiar.Location = new Point(_parar.Left - _limpiar.Width - E(10), E(7));
                int x = _seguir.Right + E(18);
                foreach (var boton in _idiomas)
                {
                    boton.Location = new Point(x, E(7));
                    x += boton.Width + E(6);
                }
            }
            barra.Resize += (s, e) => Colocar();

            AplicarIdioma();
            Colocar();

            _reloj = new System.Windows.Forms.Timer { Interval = 1000 };
            _reloj.Tick += (s, e) => Refrescar();
            _reloj.Start();

            Refrescar();
        }

        // ─── Idioma ─────────────────────────────────────────────────────────────────────────

        private void CambiarIdioma(Language cual)
        {
            if (cual == _idioma) return;
            _idioma = cual;
            PreferenciasDelServidor.Language = cual;
            _textos = LauncherTexts.Get(cual);
            AplicarIdioma();
            _cifras.Invalidate();
        }

        private void AplicarIdioma()
        {
            _seguir.Text = _textos.AutoScroll;
            _limpiar.Text = _textos.ClearButton;
            _parar.Text = _textos.StopServer;
            Redimensionar(_limpiar);
            Redimensionar(_parar);

            for (int i = 0; i < _idiomas.Count; i++)
            {
                var cual = (Language)i;
                _idiomas[i].ForeColor = cual == _idioma ? LauncherTheme.LightGold : LauncherTheme.MutedGold;
                _idiomas[i].FlatAppearance.BorderColor = cual == _idioma
                    ? LauncherTheme.GoldBorder : LauncherTheme.BorderBrown;
            }
        }

        private void Redimensionar(Button boton)
            => boton.Width = TextRenderer.MeasureText(boton.Text, boton.Font).Width + E(28);

        // ─── Las cifras ─────────────────────────────────────────────────────────────────────

        private void DefinirCifras()
        {
            // Los jugadores son las sesiones vivas del puerto de juego, no los clientes lanzados:
            // un cliente puede estar abierto sin haber llegado a entrar al mundo.
            _lista.Add(new Cifra
            {
                Etiqueta = t => t.StatPlayers,
                Tono = LauncherTheme.OnlineGreen,
                Valor = () => Network.GameNodeProxy.SesionesVivas.Count.ToString(),
            });
            _lista.Add(new Cifra
            {
                Etiqueta = t => t.StatClients,
                Tono = LauncherTheme.LightGold,
                Valor = () => $"{Network.ClientLaunchRegistry.ActiveCount}/{Network.ClientLaunchRegistry.MaximumClients}",
            });
            _lista.Add(new Cifra
            {
                Etiqueta = t => t.StatFights,
                Tono = LauncherTheme.LightGold,
                Valor = () => Handlers.FightHandler.CombatesEnCurso.ToString(),
            });
            _lista.Add(new Cifra
            {
                Etiqueta = t => t.StatMaps,
                Tono = LauncherTheme.SoftGold,
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
                Etiqueta = t => t.StatMemory,
                Tono = LauncherTheme.MutedGold,
                Valor = () => $"{GC.GetTotalMemory(false) / (1024 * 1024)} MB",
            });
            _lista.Add(new Cifra
            {
                Etiqueta = t => t.StatUptime,
                Tono = LauncherTheme.MutedGold,
                Valor = () =>
                {
                    var va = DateTime.UtcNow - _arranque;
                    return va.TotalDays >= 1 ? $"{(int)va.TotalDays}d {va.Hours}h"
                         : va.TotalHours >= 1 ? $"{(int)va.TotalHours}h {va.Minutes:00}m"
                         : $"{va.Minutes}m {va.Seconds:00}s";
                },
            });
        }

        private bool _yaAvise;

        private void PintarCifras(object? sender, PaintEventArgs e)
        {
            // Un Paint que revienta no se ve: la fila se queda en blanco y no hay ni un aviso.
            try { PintarCifrasDeVerdad(e.Graphics); }
            catch (Exception ex)
            {
                if (_yaAvise) return;
                _yaAvise = true;
                Console.WriteLine($"[Servidor] La fila de cifras no se ha podido pintar: {ex}");
            }
        }

        private void PintarCifrasDeVerdad(Graphics g)
        {
            RecortarFondo(g, _cifras);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int margen = E(22);
            int hueco = E(10);
            int alto = E(78);
            int y = E(8);
            int ancho = Math.Max(E(90), (_cifras.Width - margen * 2 - hueco * (_lista.Count - 1)) / _lista.Count);

            using var fEtiqueta = Letra(7.5f, FontStyle.Bold);
            using var pincelEtiqueta = new SolidBrush(LauncherTheme.MutedGold);
            using var relleno = new SolidBrush(LauncherTheme.CardFill);
            using var borde = new Pen(LauncherTheme.BorderBrown, Math.Max(1f, _escala));
            using var dentro = new StringFormat(StringFormatFlags.NoWrap)
            {
                Trimming = StringTrimming.EllipsisCharacter,
            };

            for (int i = 0; i < _lista.Count; i++)
            {
                var cifra = _lista[i];
                var caja = new Rectangle(margen + i * (ancho + hueco), y, ancho, alto);

                using var camino = Redondeado(caja, E(8));
                g.FillPath(relleno, camino);
                g.DrawPath(borde, camino);

                g.DrawString(cifra.Etiqueta(_textos), fEtiqueta, pincelEtiqueta,
                             new RectangleF(caja.X + E(10), caja.Y + E(9), caja.Width - E(16),
                                            fEtiqueta.GetHeight(g) + 2), dentro);

                // El cuerpo de la cifra se ajusta al ancho que haya: con la ventana maximizada
                // caben grandes, y estrechándola no se salen ni acaban en puntos suspensivos.
                float cuerpo = 17f;
                var fValor = Letra(cuerpo, FontStyle.Bold);
                while (cuerpo > 9f && g.MeasureString(cifra.Ultimo, fValor).Width > caja.Width - E(20))
                {
                    fValor.Dispose();
                    cuerpo -= 1f;
                    fValor = Letra(cuerpo, FontStyle.Bold);
                }

                using (fValor)
                using (var pincelValor = new SolidBrush(cifra.Tono))
                {
                    g.DrawString(cifra.Ultimo, fValor, pincelValor,
                                 new RectangleF(caja.X + E(10), caja.Y + E(30), caja.Width - E(16),
                                                fValor.GetHeight(g) + 2), dentro);
                }
            }
        }

        private static GraphicsPath Redondeado(Rectangle r, int radio)
        {
            var camino = new GraphicsPath();
            int d = Math.Max(2, radio * 2);
            camino.AddArc(r.X, r.Y, d, d, 180, 90);
            camino.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            camino.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            camino.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            camino.CloseFigure();
            return camino;
        }

        // ─── El fondo ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Compone el fondo: la foto recortada como haría un background-size: cover.
        ///
        /// Se compone UNA vez por tamaño y se guarda, en vez de escalar la imagen en cada
        /// repintado. Es lo que hace el lanzador y es lo que evita que la ventana vaya a tirones.
        /// </summary>
        private void ComponerFondo()
        {
            int ancho = Math.Max(1, ClientSize.Width);
            int alto = Math.Max(1, ClientSize.Height);
            if (_fondoCompuesto != null && _fondoCompuesto.Width == ancho && _fondoCompuesto.Height == alto) return;

            _fondoCompuesto?.Dispose();
            _fondoCompuesto = new Bitmap(ancho, alto);

            using var g = Graphics.FromImage(_fondoCompuesto);
            g.Clear(LauncherTheme.Background);

            if (_foto != null)
            {
                float factor = Math.Max((float)ancho / _foto.Width, (float)alto / _foto.Height);
                int anchoFoto = (int)Math.Ceiling(_foto.Width * factor);
                int altoFoto = (int)Math.Ceiling(_foto.Height * factor);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(_foto, (ancho - anchoFoto) / 2, (alto - altoFoto) / 2, anchoFoto, altoFoto);
            }

            // Un velo MUY suave, sólo el necesario para que se lea lo de encima. El primer intento
            // llevaba uno tan oscuro que la foto no se veía: parecía un fondo negro y punto.
            using var velo = new SolidBrush(Color.FromArgb(84, 8, 4, 2));
            g.FillRectangle(velo, 0, 0, ancho, alto);
        }

        /// <summary>Le da a un panel transparente el trozo de fondo que le toca.</summary>
        private void RecortarFondo(Graphics g, Control panel)
        {
            ComponerFondo();
            if (_fondoCompuesto == null) { g.Clear(LauncherTheme.Background); return; }

            var recorte = Rectangle.Intersect(
                new Rectangle(panel.Location.X, panel.Location.Y, panel.Width, panel.Height),
                new Rectangle(0, 0, _fondoCompuesto.Width, _fondoCompuesto.Height));

            g.Clear(LauncherTheme.Background);
            if (recorte.Width <= 0 || recorte.Height <= 0) return;
            g.DrawImage(_fondoCompuesto, new Rectangle(0, 0, recorte.Width, recorte.Height),
                        recorte, GraphicsUnit.Pixel);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            ComponerFondo();
            if (_fondoCompuesto != null) e.Graphics.DrawImageUnscaled(_fondoCompuesto, 0, 0);
            else base.OnPaintBackground(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ComponerFondo();
            Invalidate(true);
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
            // Sólo se repinta si algo ha cambiado, y el panel va con doble búfer: así no parpadea.
            if (cambio) _cifras.Invalidate();

            TraerRegistro();
        }

        private void TraerRegistro()
        {
            string json;
            try { json = ConsoleLogBuffer.GetLogsJson(_ultimaLinea); }
            catch { return; }

            var nuevas = new List<(long Id, string Hora, string Texto)>();
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

            _registro.SelectionColor = LauncherTheme.LogTime;
            _registro.AppendText(hora.Length > 0 ? hora + "  " : "");

            _registro.SelectionColor = ColorDe(texto);
            _registro.AppendText(texto + "\n");
        }

        /// <summary>
        /// El color de cada línea, con la misma paleta de consola que el lanzador y mirando los
        /// mismos prefijos entre corchetes que el servidor lleva escribiendo desde siempre.
        /// </summary>
        private static Color ColorDe(string linea)
        {
            if (linea.Contains("[!]") || linea.Contains("Error") || linea.Contains("error"))
                return LauncherTheme.LogError;
            if (linea.Contains("Rechazad") || linea.Contains("rechazad")) return LauncherTheme.Red;
            if (linea.Contains("[HAAPI]")) return LauncherTheme.LogHaapi;
            if (linea.Contains("[Zaap")) return LauncherTheme.LogZaap;
            if (linea.Contains("[+]")) return LauncherTheme.LogSuccess;
            if (linea.Contains("[Combate]") || linea.Contains("[FightHandler]")) return LauncherTheme.LogServer;
            if (linea.Contains("[Control]") || linea.Contains("[Comandos]")) return LauncherTheme.HighlightText;
            return LauncherTheme.LogNormal;
        }

        // ─── Botones ────────────────────────────────────────────────────────────────────────

        private Button Boton(string texto, Color tono, int ancho = 0)
        {
            var letra = Letra(8.5f, FontStyle.Bold);
            var boton = new Button
            {
                Text = texto,
                AutoSize = false,
                Height = E(30),
                Width = ancho > 0 ? ancho : TextRenderer.MeasureText(texto, letra).Width + E(28),
                FlatStyle = FlatStyle.Flat,
                BackColor = LauncherTheme.FieldBackground,
                ForeColor = tono,
                Font = letra,
                Cursor = Cursors.Hand,
            };
            boton.FlatAppearance.BorderColor = LauncherTheme.BorderBrown;
            boton.FlatAppearance.MouseOverBackColor = LauncherTheme.LightBrown;
            return boton;
        }

        private void PararloTodo(object? sender, EventArgs e)
        {
            if (!Confirmar()) return;
            _parar.Enabled = false;
            Program.RequestShutdown("botón de la ventana del servidor");
        }

        private bool Confirmar()
        {
            int dentro = Network.GameNodeProxy.SesionesVivas.Count;
            string aviso = dentro > 0
                ? string.Format(_textos.StopServerWithPlayers, dentro)
                : _textos.StopServerConfirm;
            return MessageBox.Show(aviso, "Jondo Server", MessageBoxButtons.YesNo,
                                   MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Cerrar esta ventana SÍ para el servidor: es su ventana. Pero se pregunta, porque
            // puede haber gente jugando y una X es fácil de dar sin querer.
            if (e.CloseReason == CloseReason.UserClosing && !Program.ApagandoYa)
            {
                if (!Confirmar()) { e.Cancel = true; return; }
                Program.RequestShutdown("ventana del servidor cerrada");
            }

            _reloj.Stop();
            base.OnFormClosing(e);
        }

        private void TryCargarIcono()
        {
            try
            {
                string ruta = Path.Combine(LauncherTheme.AssetsFolder, "favicon.ico");
                if (File.Exists(ruta)) Icon = new Icon(ruta);
            }
            catch { }
        }

        /// <summary>Abre la ventana en su propio hilo, para que no estorbe a los servicios.</summary>
        public static void Abrir()
        {
            var lista = new System.Threading.ManualResetEventSlim(false);
            var hilo = new System.Threading.Thread(() =>
            {
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    try { Application.SetHighDpiMode(HighDpiMode.SystemAware); } catch { }
                    var ventana = new VentanaDelServidor();
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
        }
    }
}
