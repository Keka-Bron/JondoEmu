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
    internal sealed class ServerWindow : Form, IBackgroundWindow
    {
        private Image? _foto;
        private Bitmap? _fondoCompuesto;

        /// <summary>El fondo ya compuesto, para que los paneles se recorten su trozo.</summary>
        public Image? ComposedBackground => _fondoCompuesto;

        private readonly Panel _caja;
        private readonly FlickerFreePanel _cifras;
        private readonly RichTextBox _registro;
        private readonly LauncherLogo _logo;
        private readonly System.Windows.Forms.Timer _reloj;
        private readonly CheckBox _seguir;
        private readonly LauncherButton _parar;
        private readonly LauncherButton _limpiar;
        private readonly LauncherButton _reducir;
        private readonly LauncherButton _ampliar;
        private readonly List<LauncherButton> _idiomas = new();
        private Panel? _barra;
        private readonly TextBox _filtro = new();
        private readonly Label _version = new();
        private readonly System.Windows.Forms.ToolTip _pistas = new();

        private bool _interfazLista;
        private bool _ajustandoBarra;

        private long _ultimaLinea;
        private readonly DateTime _arranque = DateTime.UtcNow;
        private readonly System.Diagnostics.Process _yo = System.Diagnostics.Process.GetCurrentProcess();

        private Language _idioma = ServerPreferences.Language;
        private LauncherTexts _textos = LauncherTexts.Get(ServerPreferences.Language);

        // ─── El escalado ──────────────────────────────────────────────────────────────────────
        //
        // Son DOS cosas multiplicadas y conviene no confundirlas:
        //
        //   * el dpi de la pantalla, que Windows escala solo y las letras —que van en puntos—
        //     siguen solas: no hay que multiplicarlas por nada;
        //   * el ampliador, para pantallas de muchas pulgadas con la escala al 100%, donde 96 dpi
        //     deja la ventana diminuta y hace falta subirlo a mano.
        //
        // _escala lleva las dos y se usa para los PIXELES (E()). Las letras van por
        // LauncherTheme.CreateFont, que ya mete el ampliador dentro; multiplicar un cuerpo por
        // _escala y además dejarlo en puntos lo escalaba dos veces —la caja de «seguir el registro»
        // y las cifras salían enormes en cuanto Windows pasaba del 100%—.
        private const float ConsoleFontPixels = 15f;
        private const float UiFontPixels = 12f;
        private const float MetricFontPixels = 11.5f;

        private float _escala;

        private int E(int px) => (int)Math.Round(px * _escala);
        private Font Letra(float cuerpo, FontStyle estilo = FontStyle.Regular)
            => LauncherTheme.CreateFont(cuerpo, estilo);
        private Font Mono(float cuerpo) => LauncherTheme.CreateMonoFont(cuerpo);

        /// <summary>Recalcula el escalado: el dpi de la ventana por el ampliador guardado.</summary>
        private float EscalaActual() => DeviceDpi / 96f * ServerPreferences.Zoom;

        /// <summary>
        /// Un panel que no parpadea al repintarse.
        ///
        /// Las cifras se refrescan cada segundo y daban un pestañeo en cada una: un Panel normal
        /// borra el fondo y luego dibuja, y entre las dos cosas se ve el hueco. Con doble búfer se
        /// compone fuera de pantalla y se vuelca de una vez.
        /// </summary>
        private sealed class FlickerFreePanel : Panel
        {
            public FlickerFreePanel()
            {
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                UpdateStyles();
            }
        }

        /// <summary>Cada cifra: su etiqueta, de dónde sale y de qué color va.</summary>
        private sealed class Metric
        {
            public Func<LauncherTexts, string> Etiqueta = _ => "";
            public Func<string> Valor = () => "";
            public Color Tono = LauncherTheme.LightGold;
            public string Ultimo = "";

            /// <summary>Si es la cabecera de un bloque en vez de un dato.</summary>
            public bool EsGrupo;
        }

        private readonly List<Metric> _lista = new();

        public ServerWindow()
        {
            // Every pixel measurement in this window goes through E().  Explicitly disabling
            // WinForms' implicit font autoscaling prevents the controls from being scaled once by
            // us and a second time by the framework at 125/150/200% DPI.
            AutoScaleMode = AutoScaleMode.None;
            _escala = EscalaActual();
            LauncherTheme.UiZoom = ServerPreferences.Zoom;

            Text = "Jondo Server";
            StartPosition = FormStartPosition.CenterScreen;
            ActualizarTamanoMinimo(Screen.PrimaryScreen?.WorkingArea);
            Size = TamanoInicial(Screen.PrimaryScreen?.WorkingArea);
            // Normal on startup: the server remains easy to reach without taking over the desktop.
            WindowState = FormWindowState.Normal;
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
                Height = E(78),
                Dock = DockStyle.Top,
            };

            // La versión del cliente al que el emulador habla, justo debajo del rótulo. Es la
            // primera cosa que conviene ver en un emulador atado a una versión: sin ella, un
            // «no conecta» puede ser cualquier cosa —y con ella, casi siempre es que el cliente
            // no es el de esta versión.
            _version.Text = "v" + Contract.Version;
            _version.Dock = DockStyle.Top;
            _version.Height = E(20);
            _version.TextAlign = ContentAlignment.MiddleCenter;
            _version.BackColor = Color.Transparent;
            _version.ForeColor = LauncherTheme.MutedGold;
            _version.Font = Letra(11f);

            // Los cuatro indicadores en UNA columna a la izquierda.
            //
            // Antes iban repartidos a los dos lados del dibujo, y era bonito pero le robaba al
            // registro la mitad del ancho: la consola se quedaba en una tira de un cuarto de
            // ventana donde cada línea se partía en tres. Un registro que hay que reconstruir
            // mentalmente no se lee. Los cuatro juntos a un lado y el resto para la consola.
            _cifras = new FlickerFreePanel
            {
                Dock = DockStyle.Left,
                Width = 0,   // lo pone AjustarConsola
                BackColor = Color.Transparent,
                AutoScroll = true,
            };
            _cifras.Paint += (s, e) => ConRed(e.Graphics, _cifras, 0, 4);
            _cifras.Scroll += (s, e) => _cifras.Invalidate();

            DefinirCifras();

            // La consola se queda con todo lo que no ocupan las cifras.
            //
            // Estuvo abajo a todo lo ancho —partía el dibujo por la mitad— y luego en una columna a
            // la derecha, que dejaba ver el dibujo entero pero le daba un cuarto de ventana. Un
            // renglón de tráfico son unos cien caracteres y ahí no caben: se partía en tres y había
            // que recomponerlo con la vista. Ahora manda el registro y el dibujo se ve por detrás.
            _caja = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                Padding = new Padding(E(12), E(12), E(14), E(12)),
            };

            var barra = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = E(48),
                BackColor = Color.Transparent,
                Padding = new Padding(E(12), 0, E(12), 0),
            };
            _barra = barra;

            _registro = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = LauncherTheme.ConsoleBackground,
                ForeColor = LauncherTheme.LogNormal,
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                // The factory accepts CSS pixels and creates a point-sized font. Windows then
                // renders those points at the monitor DPI, while UiZoom adds the user's chosen
                // enlargement exactly once. Fifteen logical pixels keeps dense logs readable
                // without turning an ultrawide console into wrapped prose.
                Font = Mono(ConsoleFontPixels),

                // Sin partir líneas. Con la consola estrecha partirlas era lo menos malo; ahora que
                // ocupa la ventana entera, un renglón es un paquete y eso vale más que no tener
                // barra abajo.
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Both,
                DetectUrls = false,
            };

            _caja.Controls.Add(_registro);
            _caja.Paint += PintarCaja;

            // El orden importa: WinForms acopla de DELANTE hacia atrás, o sea al revés del orden en
            // que se añaden. Se lee de abajo arriba: el rótulo coge su franja de arriba, la barra
            // la de abajo, las cifras la columna izquierda, y la consola —que va en Fill— se queda
            // con TODO lo que sobre.
            //
            // Por eso la consola se añade la primera aunque salga la última: en Fill hay que estar
            // al fondo de la pila o te comes el sitio de los demás.
            Controls.Add(_caja);
            Controls.Add(_cifras);
            Controls.Add(barra);
            Controls.Add(_version);
            Controls.Add(_logo);

            _seguir = new CheckBox
            {
                Checked = true,
                ForeColor = LauncherTheme.MutedGold,
                BackColor = Color.Transparent,
                AutoSize = true,
                Font = Letra(UiFontPixels),
            };
            barra.Controls.Add(_seguir);

            // El filtro del registro: con algo escrito, sólo entran en la consola las líneas que
            // lo contengan. Para buscar un error entre el ruido de una sesión larga, que es lo
            // que la consola entera no permite. Lo escrito no se pierde: sólo no se enseña.
            _filtro.ForeColor = LauncherTheme.FieldText;
            _filtro.BackColor = LauncherTheme.ConsoleBackground;
            _filtro.BorderStyle = BorderStyle.FixedSingle;
            _filtro.Font = Mono(12f);
            _filtro.Width = E(190);
            barra.Controls.Add(_filtro);

            foreach (var cual in new[] { Language.Es, Language.En, Language.Fr })
            {
                var boton = Boton(LauncherTexts.Code(cual).ToUpperInvariant(), LauncherTheme.MutedGold, E(48));
                var elegido = cual;
                boton.Click += (s, e) => CambiarIdioma(elegido);
                _idiomas.Add(boton);
                barra.Controls.Add(boton);
            }

            // La ampliación de la interfaz, con el mismo par de botones que el lanzador: para la
            // ventana que se pasa el día mirándose en una pantalla de muchas pulgadas al 100%.
            _reducir = Boton("A–", LauncherTheme.MutedGold, E(48));
            _reducir.Click += (s, e) => CambiarZoom(-0.25f);
            barra.Controls.Add(_reducir);

            _ampliar = Boton("A+", LauncherTheme.MutedGold, E(48));
            _ampliar.Click += (s, e) => CambiarZoom(+0.25f);
            barra.Controls.Add(_ampliar);

            _limpiar = Boton("", LauncherTheme.SoftGold);
            _limpiar.Click += (s, e) => _registro.Clear();
            barra.Controls.Add(_limpiar);

            _parar = Boton("", LauncherTheme.Red);
            _parar.Click += PararloTodo;
            barra.Controls.Add(_parar);

            barra.Resize += (s, e) => ColocarBarra();

            AplicarIdioma();
            AjustarConsola();
            ActualizarAreaCifras();
            ColocarBarra();

            _reloj = new System.Windows.Forms.Timer { Interval = 1000 };
            _reloj.Tick += (s, e) => Refrescar();
            _reloj.Start();

            _interfazLista = true;
            Refrescar();
        }

        // ─── Idioma ─────────────────────────────────────────────────────────────────────────

        private void CambiarIdioma(Language cual)
        {
            if (cual == _idioma) return;
            _idioma = cual;
            ServerPreferences.Language = cual;
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
            ColocarBarra();

            // Las pistas emergentes: qué idioma es cada botón —dicho en su propio idioma, que es
            // como uno reconoce el suyo—, el par de ampliación y el filtro del registro.
            string[] nombres = { "Español", "English", "Français" };
            for (int i = 0; i < _idiomas.Count && i < nombres.Length; i++)
                _pistas.SetToolTip(_idiomas[i], nombres[i]);
            _pistas.SetToolTip(_reducir, _textos.ZoomTooltip);
            _pistas.SetToolTip(_ampliar, _textos.ZoomTooltip);
            _pistas.SetToolTip(_filtro, _textos.LogFilterHint);

            for (int i = 0; i < _idiomas.Count; i++)
            {
                var cual = (Language)i;
                _idiomas[i].TextColor = cual == _idioma ? LauncherTheme.LightGold : LauncherTheme.MutedGold;
                _idiomas[i].Active = cual == _idioma;
                _idiomas[i].BorderColor = cual == _idioma
                    ? LauncherTheme.GoldBorder : LauncherTheme.BorderBrown;
            }
        }

        private void Redimensionar(LauncherButton boton)
            => boton.Width = TextRenderer.MeasureText(boton.Text, boton.Font).Width + E(30);

        // ─── Ventana y barra adaptables ────────────────────────────────────────────────────

        /// <summary>
        /// Keeps the normal startup window inside the usable desktop. Physical pixels are used
        /// here because Screen.WorkingArea and Form.Size use them in a DPI-aware process.
        /// </summary>
        private void ActualizarTamanoMinimo(Rectangle? area)
        {
            int ancho = E(860);
            int alto = E(560);
            if (area.HasValue)
            {
                ancho = Math.Min(ancho, Math.Max(640, area.Value.Width - E(24)));
                alto = Math.Min(alto, Math.Max(420, area.Value.Height - E(24)));
            }
            MinimumSize = new Size(ancho, alto);
        }

        private Size TamanoInicial(Rectangle? area)
        {
            int ancho = E(1180);
            int alto = E(700);
            if (area.HasValue)
            {
                ancho = Math.Min(ancho, Math.Max(640, area.Value.Width - E(32)));
                alto = Math.Min(alto, Math.Max(420, area.Value.Height - E(32)));
            }
            return new Size(Math.Max(MinimumSize.Width, ancho),
                            Math.Max(MinimumSize.Height, alto));
        }

        private void LimitarVentanaAlArea(Rectangle area, bool centrar)
        {
            if (WindowState != FormWindowState.Normal) return;

            int margen = E(12);
            int anchoMaximo = Math.Max(640, area.Width - margen * 2);
            int altoMaximo = Math.Max(420, area.Height - margen * 2);
            int ancho = Math.Max(MinimumSize.Width, Math.Min(Width, anchoMaximo));
            int alto = Math.Max(MinimumSize.Height, Math.Min(Height, altoMaximo));
            int x = centrar
                ? area.Left + (area.Width - ancho) / 2
                : Math.Max(area.Left, Math.Min(Left, area.Right - ancho));
            int y = centrar
                ? area.Top + (area.Height - alto) / 2
                : Math.Max(area.Top, Math.Min(Top, area.Bottom - alto));
            Bounds = new Rectangle(x, y, ancho, alto);
        }

        /// <summary>
        /// Places every toolbar item without overlap. On a narrow laptop the destructive actions
        /// move to a second row; on wider and ultrawide displays everything stays in one compact
        /// row. The filter is the only elastic-width item.
        /// </summary>
        private void ColocarBarra()
        {
            if (_barra == null || _ajustandoBarra) return;

            _ajustandoBarra = true;
            try
            {
                int margen = E(12);
                int hueco = E(6);
                int separacion = E(14);
                int altoFila = E(44);

                Size seguir = _seguir.GetPreferredSize(Size.Empty);
                _seguir.Size = seguir;

                int idiomas = 0;
                foreach (var boton in _idiomas) idiomas += boton.Width + hueco;
                idiomas = Math.Max(0, idiomas - hueco);
                int zoom = _reducir.Width + hueco + _ampliar.Width;
                int selectores = idiomas + hueco + zoom;

                int acciones = _limpiar.Width + hueco + _parar.Width;
                int sinFiltro = seguir.Width + separacion + separacion + selectores;
                int disponibleUnaFila = _barra.ClientSize.Width - margen * 2 - sinFiltro
                                       - separacion - acciones;
                bool dosFilas = disponibleUnaFila < E(100);

                // At very high zoom on a low-resolution display, moving only the actions is not
                // enough. Move the A-/A+ pair with them; if even that row cannot fit beside the
                // translated stop label, give the actions a third row.
                int disponibleConDosFilas = _barra.ClientSize.Width - margen * 2 - sinFiltro;
                bool zoomSegundaFila = dosFilas && disponibleConDosFilas < E(80);
                if (zoomSegundaFila)
                    sinFiltro = seguir.Width + separacion + separacion + idiomas;

                int disponibleSinZoom = _barra.ClientSize.Width - margen * 2 - sinFiltro;
                bool gruposSeparados = zoomSegundaFila && disponibleSinZoom < E(48);
                if (gruposSeparados)
                    sinFiltro = seguir.Width + separacion;

                int segundaFila = zoomSegundaFila ? zoom + separacion + acciones : acciones;
                bool tresFilas = gruposSeparados || (zoomSegundaFila
                               && segundaFila > _barra.ClientSize.Width - margen * 2);

                int disponibleFiltro = dosFilas
                    ? _barra.ClientSize.Width - margen * 2 - sinFiltro
                    : disponibleUnaFila;
                _filtro.Width = Math.Min(E(210), Math.Max(E(48), disponibleFiltro));

                int filas = tresFilas ? 3 : dosFilas ? 2 : 1;
                int altoNecesario = altoFila * filas + E(4);
                if (_barra.Height != altoNecesario) _barra.Height = altoNecesario;

                int CentroY(Control control, int fila)
                    => fila * altoFila + Math.Max(0, (altoFila - control.Height) / 2);

                int x = margen;
                _seguir.Location = new Point(x, CentroY(_seguir, 0));
                x = _seguir.Right + separacion;

                _filtro.Location = new Point(x, CentroY(_filtro, 0));
                x = _filtro.Right + separacion;

                int filaSelectores = gruposSeparados ? 1 : 0;
                if (gruposSeparados) x = margen;
                foreach (var boton in _idiomas)
                {
                    boton.Location = new Point(x, CentroY(boton, filaSelectores));
                    x = boton.Right + hueco;
                }

                int filaZoom = gruposSeparados ? 1 : zoomSegundaFila ? 1 : 0;
                int zoomX = zoomSegundaFila && !gruposSeparados ? margen : x;
                _reducir.Location = new Point(zoomX, CentroY(_reducir, filaZoom));
                _ampliar.Location = new Point(_reducir.Right + hueco,
                                              CentroY(_ampliar, filaZoom));

                int filaAcciones = tresFilas ? 2 : dosFilas ? 1 : 0;
                int accionesX = Math.Max(margen, _barra.ClientSize.Width - margen - acciones);
                _limpiar.Location = new Point(accionesX, CentroY(_limpiar, filaAcciones));
                _parar.Location = new Point(_limpiar.Right + hueco, CentroY(_parar, filaAcciones));
            }
            finally
            {
                _ajustandoBarra = false;
            }
        }

        // ─── La ampliación ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Sube o baja el tamaño de la interfaz y lo aplica en el acto.
        ///
        /// No basta con recolocar: las fuentes se crearon con el ampliador de antes y hay que
        /// rehacerlas con el de ahora, y lo mismo los altos que se clavaron en el constructor —la
        /// barra de abajo, el rótulo de arriba—. Queda guardado en la preferencia del servidor para
        /// el próximo arranque.
        /// </summary>
        private void CambiarZoom(float paso)
        {
            float antes = ServerPreferences.Zoom;
            float despues = MathF.Max(0.75f, MathF.Min(2f, antes + paso));
            if (MathF.Abs(despues - antes) < 0.01f) return;

            ServerPreferences.Zoom = despues;
            AplicarZoom();
        }

        private void AplicarZoom()
        {
            LauncherTheme.UiZoom = ServerPreferences.Zoom;
            _escala = EscalaActual();
            ActualizarTamanoMinimo(Screen.FromControl(this).WorkingArea);

            ReemplazarFuente(_registro, Mono(ConsoleFontPixels));
            ReemplazarFuente(_seguir, Letra(UiFontPixels));
            _logo.Height = E(78);
            _version.Height = E(20);
            ReemplazarFuente(_version, Letra(11f));
            ReemplazarFuente(_filtro, Mono(12f));
            _filtro.Width = E(190);
            _caja.Padding = new Padding(E(12), E(12), E(14), E(12));
            if (_barra != null) _barra.Padding = new Padding(E(12), 0, E(12), 0);

            foreach (var boton in _idiomas)
            {
                AplicarEscalaBoton(boton);
                boton.Width = E(48);
            }
            AplicarEscalaBoton(_reducir); _reducir.Width = E(48);
            AplicarEscalaBoton(_ampliar); _ampliar.Width = E(48);
            AplicarEscalaBoton(_limpiar);
            AplicarEscalaBoton(_parar);
            Redimensionar(_limpiar);
            Redimensionar(_parar);

            AjustarConsola();
            ActualizarAreaCifras();
            ColocarBarra();
            ComponerFondo();
            Invalidate(true);
        }

        private void AplicarEscalaBoton(LauncherButton boton)
        {
            ReemplazarFuente(boton, Letra(12f, FontStyle.Bold));
            boton.Height = E(34);
            boton.CornerRadius = E(5);
            boton.BorderWidth = Math.Max(1, E(1));
            boton.LetterSpacing = Math.Max(1f, _escala);
        }

        private static void ReemplazarFuente(Control control, Font nueva)
        {
            Font anterior = control.Font;
            control.Font = nueva;
            if (!ReferenceEquals(anterior, Control.DefaultFont)) anterior.Dispose();
        }

        // ─── Las cifras ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Qué se enseña, en su orden y agrupado.
        ///
        /// Agrupadas y en columna, no en una fila apretada arriba: así caben las que hacen falta
        /// para llevar un servidor —tráfico, CPU, hilos— y se leen de un vistazo por bloques.
        ///
        /// Todas salen de algo que el servidor sabe de verdad. Ninguna está puesta para rellenar:
        /// si no se puede medir, no se enseña.
        /// </summary>
        private void DefinirCifras()
        {
            Grupo(t => t.GroupWorld);

            // Conectados es el número de sockets de juego vivos. "En el mundo" son los que además
            // han llegado a entrar a un mapa: entre una cosa y otra hay unos segundos de carga, y
            // con un cliente atascado la diferencia se queda ahí y se ve.
            Cifra_(t => t.StatPlayers, LauncherTheme.OnlineGreen,
                   () => $"{Network.GameNodeProxy.SesionesVivas.Count}/{Contract.ClientesEnTotal}");
            Cifra_(t => t.StatInWorld, LauncherTheme.DotGreen, () =>
            {
                int dentro = 0;
                foreach (var s in Network.GameNodeProxy.SesionesVivas.Values) if (s.IsInWorld) dentro++;
                return dentro.ToString();
            });
            Cifra_(t => t.StatFights, LauncherTheme.Red,
                   () => Handlers.FightHandler.CombatesEnCurso.ToString());
            Cifra_(t => t.StatMaps, LauncherTheme.LogHaapi, () =>
            {
                var mapas = new HashSet<long>();
                foreach (var s in Network.GameNodeProxy.SesionesVivas.Values)
                {
                    if (s.IsInWorld) mapas.Add(s.MapId);
                }
                return mapas.Count.ToString();
            });
            Cifra_(t => t.StatClients, LauncherTheme.LightGold,
                   () => Network.ClientLaunchRegistry.ActiveCount.ToString());

            Grupo(t => t.GroupNetwork);

            // Lo que ha pasado por los sockets desde que arrancó, y a qué ritmo va ahora. El ritmo
            // es lo que dice si el servidor está haciendo algo: los totales sólo dicen que hizo.
            Cifra_(t => t.StatSent, LauncherTheme.LogSuccess,
                   () => $"{Bonito(Jondo.Protocol.NetworkMessage.BytesFuera)}  " +
                         $"({Miles(Jondo.Protocol.NetworkMessage.PaquetesFuera)})");
            Cifra_(t => t.StatReceived, LauncherTheme.LogZaap,
                   () => $"{Bonito(Jondo.Protocol.NetworkMessage.BytesDentro)}  " +
                         $"({Miles(Jondo.Protocol.NetworkMessage.PaquetesDentro)})");
            Cifra_(t => t.StatRate, LauncherTheme.LightGold, () => $"{_porSegundo:0.0} KB/s");

            Grupo(t => t.GroupMachine);

            Cifra_(t => t.StatCpu, LauncherTheme.LogHaapi, () => $"{_cpu:0.0} %");
            Cifra_(t => t.StatMemory, LauncherTheme.SoftGold, () =>
            {
                _yo.Refresh();
                return Bonito(_yo.WorkingSet64);
            });
            Cifra_(t => t.StatThreads, LauncherTheme.LogZaap, () =>
            {
                _yo.Refresh();
                return _yo.Threads.Count.ToString();
            });
            Cifra_(t => t.StatUptime, LauncherTheme.OnlineGreen, () =>
            {
                var va = DateTime.UtcNow - _arranque;
                return va.TotalDays >= 1 ? $"{(int)va.TotalDays}d {va.Hours}h"
                     : va.TotalHours >= 1 ? $"{(int)va.TotalHours}h {va.Minutes:00}m"
                     : $"{va.Minutes}m {va.Seconds:00}s";
            });

            Grupo(t => t.GroupLoaded);

            // Esto no cambia en toda la ejecución, pero dice de un vistazo si el mundo se cargó
            // entero o si algo se quedó a medias, que es lo primero que uno quiere saber.
            Cifra_(t => t.StatWorldMaps, LauncherTheme.SoftGold,
                   () => Miles(Managers.MobSpawnManager.MapasConGrupos));
            Cifra_(t => t.StatWorldGroups, LauncherTheme.MutedGold,
                   () => Miles(Managers.MobSpawnManager.TotalGrupos));
            Cifra_(t => t.StatWorldNpcs, LauncherTheme.HighlightText,
                   () => Miles(Managers.Npcs.Count));
        }

        private void Grupo(Func<LauncherTexts, string> titulo)
            => _lista.Add(new Metric { Etiqueta = titulo, EsGrupo = true });

        private void Cifra_(Func<LauncherTexts, string> etiqueta, Color tono, Func<string> valor)
            => _lista.Add(new Metric { Etiqueta = etiqueta, Tono = tono, Valor = valor });

        private static string Bonito(long bytes)
            => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):0.0} GB"
             : bytes >= 1024L * 1024 ? $"{bytes / (1024.0 * 1024):0} MB"
             : bytes >= 1024 ? $"{bytes / 1024.0:0} KB"
             : $"{bytes} B";

        private static string Miles(long cuantos) => cuantos.ToString("N0");

        // ─── CPU y ritmo, que hay que medirlos entre dos momentos ───────────────────────────

        private TimeSpan _cpuAntes;
        private DateTime _cuandoAntes = DateTime.UtcNow;
        private long _bytesAntes;
        private double _cpu;
        private double _porSegundo;

        private void MedirRitmos()
        {
            try
            {
                _yo.Refresh();
                var ahora = DateTime.UtcNow;
                double segundos = (ahora - _cuandoAntes).TotalSeconds;
                if (segundos <= 0) return;

                var cpuAhora = _yo.TotalProcessorTime;
                // Entre todos los núcleos: si no, un servidor usando un núcleo entero de ocho
                // marcaría 100% y parecería que está ahogado cuando le sobran siete.
                _cpu = (cpuAhora - _cpuAntes).TotalSeconds / segundos / Environment.ProcessorCount * 100.0;
                _cpuAntes = cpuAhora;

                long bytesAhora = Jondo.Protocol.NetworkMessage.BytesFuera +
                                  Jondo.Protocol.NetworkMessage.BytesDentro;
                _porSegundo = (bytesAhora - _bytesAntes) / 1024.0 / segundos;
                _bytesAntes = bytesAhora;

                _cuandoAntes = ahora;
            }
            catch { }
        }

        private bool _yaAvise;

        private void ActualizarAreaCifras()
        {
            if (_cifras == null) return;

            int y = E(6);
            for (int i = 0; i < _lista.Count; i++)
            {
                if (!_lista[i].EsGrupo) continue;
                int cuantas = 0;
                for (int j = i + 1; j < _lista.Count && !_lista[j].EsGrupo; j++) cuantas++;
                y += E(27) + cuantas * E(23) + E(7) + E(8);
            }
            _cifras.AutoScrollMinSize = new Size(0, y);
        }

        /// <summary>
        /// Envuelve el pintado de una columna. Un Paint que revienta no se ve: la columna se queda
        /// en blanco y no hay ni un aviso. Ya pasó una vez y costó más de lo que debería.
        /// </summary>
        private void ConRed(Graphics g, Panel panel, int desde, int cuantos)
        {
            try { PintarColumna(g, panel, desde, cuantos); }
            catch (Exception ex)
            {
                if (_yaAvise) return;
                _yaAvise = true;
                Console.WriteLine($"[Servidor] Una columna de cifras no se ha podido pintar: {ex}");
            }
        }

        /// <summary>
        /// La columna de cifras: una tarjeta por bloque y dentro una línea por dato.
        ///
        /// La primera versión era una fila de tarjetas apretadas arriba. Cabían seis y ya iban
        /// justas; en columna caben las quince que hacen falta para llevar un servidor, y agrupadas
        /// se leen por bloques en vez de como una ristra.
        /// </summary>
        private void PintarColumna(Graphics g, Panel panel, int desdeGrupo, int cuantosGrupos)
        {
            RecortarFondo(g, panel);
            var estado = g.Save();
            try
            {
                // ScrollableControl moves child controls automatically, but custom painting needs
                // the scroll offset explicitly. This keeps every metrics card reachable on short
                // laptop screens instead of silently dropping the last card.
                g.TranslateTransform(panel.AutoScrollPosition.X, panel.AutoScrollPosition.Y);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                int margen = E(12);
                int ancho = panel.ClientSize.Width - margen * 2;
                if (ancho <= 0) return;

                using var fGrupo = Letra(12.5f, FontStyle.Bold);
                using var fEtiqueta = Letra(MetricFontPixels);
                using var fValor = Letra(MetricFontPixels, FontStyle.Bold);
                using var pincelGrupo = new SolidBrush(LauncherTheme.SoftGold);
                using var pincelEtiqueta = new SolidBrush(LauncherTheme.MutedGold);
                using var relleno = new SolidBrush(LauncherTheme.CardFill);
                using var borde = new Pen(LauncherTheme.BorderBrown, Math.Max(1f, _escala));
                using var izquierda = new StringFormat(StringFormatFlags.NoWrap)
                {
                    Trimming = StringTrimming.EllipsisCharacter,
                };
                using var derecha = new StringFormat(StringFormatFlags.NoWrap)
                {
                    Trimming = StringTrimming.EllipsisCharacter,
                    Alignment = StringAlignment.Far,
                };

                int altoLinea = E(23);
                int y = E(6);

                int vistos = -1;
                for (int i = 0; i < _lista.Count; i++)
                {
                    if (!_lista[i].EsGrupo) continue;
                    vistos++;
                    if (vistos < desdeGrupo) continue;
                    if (vistos >= desdeGrupo + cuantosGrupos) break;

                    int cuantas = 0;
                    for (int j = i + 1; j < _lista.Count && !_lista[j].EsGrupo; j++) cuantas++;

                    int altoCaja = E(27) + cuantas * altoLinea + E(7);
                    var caja = new Rectangle(margen, y, ancho, altoCaja);

                    using (var camino = Redondeado(caja, E(7)))
                    {
                        g.FillPath(relleno, camino);
                        g.DrawPath(borde, camino);
                    }

                    g.DrawString(_lista[i].Etiqueta(_textos), fGrupo, pincelGrupo,
                                 new RectangleF(caja.X + E(10), caja.Y + E(6), caja.Width - E(20),
                                                fGrupo.GetHeight(g) + E(2)), izquierda);

                    int linea = caja.Y + E(27);
                    for (int j = i + 1; j < _lista.Count && !_lista[j].EsGrupo; j++)
                    {
                        var cifra = _lista[j];
                        var hueco = new RectangleF(caja.X + E(10), linea,
                                                   caja.Width - E(20), altoLinea);

                        g.DrawString(cifra.Etiqueta(_textos), fEtiqueta, pincelEtiqueta,
                                     new RectangleF(hueco.X, hueco.Y + E(2), hueco.Width,
                                                    fEtiqueta.GetHeight(g) + E(2)), izquierda);

                        using var pincelValor = new SolidBrush(cifra.Tono);
                        g.DrawString(cifra.Ultimo, fValor, pincelValor,
                                     new RectangleF(hueco.X, hueco.Y + E(2), hueco.Width,
                                                    fValor.GetHeight(g) + E(2)), derecha);
                        linea += altoLinea;
                    }

                    y = caja.Bottom + E(8);
                }
            }
            finally
            {
                g.Restore(estado);
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
            AjustarConsola();
            ComponerFondo();
            Invalidate(true);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            // StartPosition chooses the monitor, then this final clamp accounts for its taskbar
            // and working area. It runs once, before the operator has had a chance to resize.
            Rectangle area = Screen.FromControl(this).WorkingArea;
            ActualizarTamanoMinimo(area);
            LimitarVentanaAlArea(area, centrar: true);
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            // A minimum expressed in the previous monitor's physical pixels can reject the
            // smaller suggested bounds when moving from 200% to 100%. Release it before WinForms
            // applies that rectangle, then rebuild it for the new monitor below.
            MinimumSize = Size.Empty;
            base.OnDpiChanged(e);
            _escala = e.DeviceDpiNew / 96f * ServerPreferences.Zoom;
            if (_interfazLista)
            {
                AplicarZoom();
                LimitarVentanaAlArea(Screen.FromControl(this).WorkingArea, centrar: false);
            }
        }

        /// <summary>
        /// Gives the metrics a bounded, proportional column and leaves the majority of every
        /// normal-width window to the log. On exceptionally narrow high-zoom desktops it falls
        /// back to a percentage so both regions remain reachable.
        /// </summary>
        private void AjustarConsola()
        {
            // Puede llegar antes de tiempo: restaurar el estado de la ventana en el constructor ya
            // dispara un OnResize, y en ese momento todavía no hay panel que ajustar. Sin esta
            // línea la ventana ni se abría -y el fallo salía como un escueto "Object reference
            // not set" en el registro-.
            if (_caja == null) return;

            int columna = Math.Max(E(260),
                                   Math.Min((int)(ClientSize.Width * 0.24f), E(340)));
            int conConsolaLegible = ClientSize.Width - E(520);
            if (conConsolaLegible >= E(210))
                columna = Math.Min(columna, conConsolaLegible);
            else
                columna = Math.Max(1, (int)(ClientSize.Width * 0.32f));

            if (_cifras != null && _cifras.Width != columna) _cifras.Width = columna;
        }

        /// <summary>
        /// El fondo detrás de la consola, y un marco alrededor.
        ///
        /// El marco no es adorno: el cuadro de texto es opaco —WinForms no sabe hacerlo
        /// translúcido— así que sin un borde parecería un agujero negro pegado encima del dibujo.
        /// </summary>
        private void PintarCaja(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            RecortarFondo(g, _caja);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var dentro = new Rectangle(
                _caja.Padding.Left - E(2), _caja.Padding.Top - E(2),
                _caja.Width - _caja.Padding.Horizontal + E(4),
                _caja.Height - _caja.Padding.Vertical + E(4));

            if (dentro.Width <= 0 || dentro.Height <= 0) return;
            using var camino = Redondeado(dentro, E(6));

            // La consola tiene que ser una PIEZA, no un rectángulo de texto flotando.
            //
            // Aquí sólo se dibujaba una raya marrón fina, y por el hueco entre esa raya y la caja
            // de texto se veía el dibujo del fondo: el área donde se escribe quedaba del mismo
            // color que todo lo demás y no se sabía dónde empezaba. Un relleno oscuro y traslúcido
            // —el mismo de la consola del lanzador— la separa del fondo sin taparlo, y el borde
            // dorado le pone el marco que el marrón no llegaba a marcar.
            using (var relleno = new SolidBrush(LauncherTheme.ConsoleFill)) g.FillPath(relleno, camino);
            using var borde = new Pen(LauncherTheme.GoldBorder, Math.Max(2f, E(2)));
            g.DrawPath(borde, camino);
        }

        // ─── El latido ──────────────────────────────────────────────────────────────────────

        private void Refrescar()
        {
            MedirRitmos();

            bool cambio = false;
            foreach (var cifra in _lista)
            {
                if (cifra.EsGrupo) continue;
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

            // El filtro del registro, si hay algo escrito: las líneas que no lo llevan no se
            // enseñan, pero el cursor sí avanza — así, al quitar el filtro, lo que llega después
            // sigue donde tiene que seguir y no se repite ni se salta nada.
            string filtro = _filtro.Text.Trim();

            foreach (var (id, hora, texto) in nuevas)
            {
                if (id > _ultimaLinea) _ultimaLinea = id;
                if (filtro.Length > 0 && texto.IndexOf(filtro, StringComparison.OrdinalIgnoreCase) < 0) continue;
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

        /// <summary>
        /// Un renglón de tráfico, tal y como lo escribe NetworkMessage:
        ///
        ///   1579 [server&gt;client] kuf (CharacterExperienceGainEvent) { 1: 453 }        3 B
        ///
        /// Se reconoce por la forma entera y no por un trozo suelto, para que un mensaje normal del
        /// servidor que casualmente lleve corchetes no acabe pintado como si fuera un paquete.
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex Paquete = new(
            @"^(\s*\d+) (\[(?:client>server|server>client)\]) ([a-z]{3})( \([A-Za-z0-9_]+\))?(.*?)(\s+\d+ B)\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        private void Escribir(string hora, string texto)
        {
            // El registro se escribe en español; si quien mira ha elegido inglés o francés, la
            // línea se traduce AL ENSEÑARLA, no al escribirla: el fichero de disco se queda tal
            // como lo escribió el servidor, que es el que se manda cuando alguien pregunta. Las
            // líneas que ya estaban en pantalla conservan el idioma de cuando entraron.
            texto = TraduccionRegistro.Traducir(texto, _idioma);

            _registro.SelectionStart = _registro.TextLength;
            _registro.SelectionLength = 0;

            _registro.SelectionColor = LauncherTheme.LogTime;
            _registro.AppendText(hora.Length > 0 ? hora + "  " : "");

            var paquete = Paquete.Match(texto);
            if (paquete.Success) { Paquete_(paquete); return; }

            _registro.SelectionColor = ColorDe(texto);
            _registro.AppendText(texto + "\n");
        }

        /// <summary>
        /// El renglón de un paquete, cada trozo de su color.
        ///
        /// Los colores no son decorativos: el ojo busca el opcode, que va en claro, y el resto se
        /// aparta. La dirección lleva el mismo código que el resto del emulador —azul lo que baja
        /// del servidor, dorado lo que sube del cliente— para no tener que leerla.
        /// </summary>
        private void Paquete_(System.Text.RegularExpressions.Match m)
        {
            void Trozo(string texto, Color color)
            {
                if (texto.Length == 0) return;
                _registro.SelectionColor = color;
                _registro.AppendText(texto);
            }

            string direccion = m.Groups[2].Value;

            Trozo(m.Groups[1].Value + " ", LauncherTheme.LogTime);
            Trozo(direccion + " ", direccion.Contains("server>") ? LauncherTheme.LogZaap : LauncherTheme.LogServer);
            Trozo(m.Groups[3].Value, LauncherTheme.HighlightText);
            Trozo(m.Groups[4].Value, LauncherTheme.LogNormal);
            Trozo(m.Groups[5].Value, LauncherTheme.LightBrownText);
            Trozo(m.Groups[6].Value + "\n", LauncherTheme.LogTime);
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

        /// <summary>
        /// Un botón con el estilo del lanzador.
        ///
        /// Antes eran <see cref="Button"/> de WinForms en plano, con su rectángulo y su letra del
        /// sistema: al lado del resto de la ventana cantaban muchísimo. LauncherButton es el mismo
        /// control que usa el lanzador —degradado, esquinas redondeadas, letra espaciada— y ahora
        /// vive en el contrato para que lo puedan usar los dos.
        /// </summary>
        private LauncherButton Boton(string texto, Color tono, int ancho = 0)
        {
            var boton = new LauncherButton
            {
                Text = texto,
                Font = Letra(12f, FontStyle.Bold),
                Height = E(34),
                Width = ancho > 0 ? ancho : E(120),
                LetterSpacing = Math.Max(1f, _escala),
                CornerRadius = E(5),
                BorderWidth = Math.Max(1, E(1)),
                BackgroundTop = Color.FromArgb(200, 34, 21, 12),
                BackgroundBottom = Color.FromArgb(200, 22, 13, 8),
                BackgroundTopHighlight = LauncherTheme.LightBrown,
                BackgroundBottomHighlight = Color.FromArgb(130, 75, 35),
                BorderColor = LauncherTheme.BorderBrown,
                BorderColorHighlight = LauncherTheme.GoldBorder,
                TextColor = tono,
                TextColorHighlight = Color.White,
                TextShadow = true,
                Cursor = Cursors.Hand,
            };
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
                // El del servidor es el mismo huevo del lanzador pero en azul claro, para
                // distinguir de un vistazo las dos ventanas en la barra de tareas.
                foreach (string nombre in new[] { "icono_servidor.ico", "favicon.ico" })
                {
                    string ruta = Path.Combine(LauncherTheme.AssetsFolder, nombre);
                    if (File.Exists(ruta)) { Icon = new Icon(ruta); return; }
                }
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
                    // Per-monitor V2 lets the manual pixel scale be recomputed when this operator
                    // window is moved between, for example, a 100% laptop and a 200% 4K display.
                    try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    var ventana = new ServerWindow();
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
                Name = "ServerWindow",
                IsBackground = true,
            };
            hilo.SetApartmentState(System.Threading.ApartmentState.STA);
            hilo.Start();
            lista.Wait(TimeSpan.FromSeconds(10));
        }
    }
}
