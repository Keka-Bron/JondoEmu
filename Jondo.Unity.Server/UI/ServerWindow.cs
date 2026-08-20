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
        private readonly LauncherLogView _registro;
        private readonly LauncherLogo _logo;
        private readonly System.Windows.Forms.Timer _reloj;
        private readonly CheckBox _seguir;
        private readonly LauncherButton _parar;
        private readonly LauncherButton _limpiar;
        private readonly List<LauncherButton> _idiomas = new();

        private long _ultimaLinea;
        private readonly DateTime _arranque = DateTime.UtcNow;
        private readonly System.Diagnostics.Process _yo = System.Diagnostics.Process.GetCurrentProcess();

        private Language _idioma = ServerPreferences.Language;
        private LauncherTexts _textos = LauncherTexts.Get(ServerPreferences.Language);

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
            _foto = Espejar(LauncherTheme.LoadImage("servidor_fondo.jpg") ?? LauncherTheme.LoadImage("bg.jpg"));

            _logo = new LauncherLogo
            {
                Primera = "JONDO",
                Segunda = "SERVER",
                BackColor = Color.Transparent,
                Height = E(92),
                Dock = DockStyle.Top,
            };

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
            };
            _cifras.Paint += (s, e) => ConRed(e.Graphics, _cifras, 0, 4);

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
                Padding = new Padding(E(10), E(14), E(22), E(10)),
            };

            var barra = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = E(40),
                BackColor = Color.Transparent,
                Padding = new Padding(E(22), 0, E(22), 0),
            };

            // El registro se pinta a mano para que se vea el dibujo por detrás.
            //
            // Con un RichTextBox no había manera: un cuadro de texto de WinForms es opaco y no hay
            // color transparente que lo cambie, así que el registro era un rectángulo negro pegado
            // encima de Nox. LauncherLogView hereda de LauncherPanel, que recorta el trozo de fondo
            // que le toca y le pone encima las capas de color que se le digan; el velo oscuro que
            // hace legible el texto es una capa con alfa y por debajo se sigue viendo el dibujo.
            //
            // Se paga con el copiar y pegar: son líneas dibujadas, no texto seleccionable. El
            // registro entero se sigue escribiendo en logs\, que es de donde hay que sacarlo.
            _registro = new LauncherLogView
            {
                Dock = DockStyle.Fill,
                ForeColor = LauncherTheme.LogNormal,
                CornerRadius = E(6),
                BorderColor = LauncherTheme.GoldBorder,
                BorderWidth = Math.Max(2, E(2)),
                Padding = new Padding(E(10), E(8), E(10), E(8)),

                // La letra va en PÍXELES y sin multiplicar por la escala del monitor.
                //
                // Antes era «Mono(6f)», que por dentro hacía 6 × DeviceDpi/96. Un tamaño en puntos
                // ya es independiente del DPI, así que multiplicarlo lo duplica: en un monitor a
                // 192 ppp salía a doce puntos y por eso el registro se veía enorme y se partía. Es
                // el mismo fallo que ya se corrigió en el desofuscador.
                Font = LauncherTheme.CreateMonoFont(12f),
            };
            _registro.Layers.Add(LauncherTheme.ConsoleFill);

            // Clic derecho sobre un paquete para decir qué es. Los nombres reales están en el
            // cliente pero huérfanos —nada dice cuál va con cuál—, así que la ligadura la hace una
            // persona con el paquete delante, eligiendo de la lista real. Lo que se elige se guarda
            // y a partir de ahí sale en el registro.
            _registro.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) Bautizar(e.Location); };

            _caja.Controls.Add(_registro);

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
            Controls.Add(_logo);

            _seguir = new CheckBox
            {
                Checked = true,
                ForeColor = LauncherTheme.MutedGold,
                BackColor = Color.Transparent,
                AutoSize = true,
                Font = LauncherTheme.CreateFont(11f * _escala),
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
            _limpiar.Click += (s, e) => _registro.Wipe();
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
            AjustarConsola();
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
            => boton.Width = TextRenderer.MeasureText(boton.Text, boton.Font).Width + E(34);

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

        /// <summary>
        /// Envuelve el pintado de una columna. Un Paint que revienta no se ve: la columna se queda
        /// en blanco y no hay ni un aviso. Ya pasó una vez y costó más de lo que debería.
        /// </summary>
        private void ConRed(Graphics g, Control panel, int desde, int cuantos)
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
        private void PintarColumna(Graphics g, Control panel, int desdeGrupo, int cuantosGrupos)
        {
            RecortarFondo(g, panel);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int margen = E(12);
            int ancho = panel.Width - margen * 2;
            if (ancho <= 0) return;

            using var fGrupo = LauncherTheme.CreateFont(10f * _escala, FontStyle.Bold);
            using var fEtiqueta = LauncherTheme.CreateFont(9.5f * _escala);
            // El valor va del MISMO tamaño que su etiqueta, sólo que en negrita y con color. Iba
            // tres puntos más grande y en bloques como el de RED —donde el valor es "0,0 KB/s" y
            // no un número corto— la línea quedaba descuadrada: la palabra pequeña y el dato
            // enorme al lado, sin ninguna razón.
            using var fValor = LauncherTheme.CreateFont(9.5f * _escala, FontStyle.Bold);
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

            int altoLinea = E(21);
            int y = E(6);

            // Se pintan sólo los bloques que le tocan a ESTA columna: la de la izquierda lleva los
            // dos primeros y la de la derecha los dos siguientes.
            int vistos = -1;
            for (int i = 0; i < _lista.Count; i++)
            {
                if (!_lista[i].EsGrupo) continue;
                vistos++;
                if (vistos < desdeGrupo) continue;
                if (vistos >= desdeGrupo + cuantosGrupos) break;

                int cuantas = 0;
                for (int j = i + 1; j < _lista.Count && !_lista[j].EsGrupo; j++) cuantas++;

                int altoCaja = E(20) + cuantas * altoLinea + E(8);
                var caja = new Rectangle(margen, y, ancho, altoCaja);
                if (caja.Bottom > panel.Height) break;

                using (var camino = Redondeado(caja, E(7)))
                {
                    g.FillPath(relleno, camino);
                    g.DrawPath(borde, camino);
                }

                g.DrawString(_lista[i].Etiqueta(_textos), fGrupo, pincelGrupo,
                             new RectangleF(caja.X + E(10), caja.Y + E(5), caja.Width - E(20),
                                            fGrupo.GetHeight(g) + 2), izquierda);

                int linea = caja.Y + E(22);
                for (int j = i + 1; j < _lista.Count && !_lista[j].EsGrupo; j++)
                {
                    var cifra = _lista[j];
                    var hueco = new RectangleF(caja.X + E(10), linea, caja.Width - E(20), altoLinea);

                    // Los dos ocupan el ANCHO ENTERO, uno pegado a la izquierda y el otro a la
                    // derecha, en vez de repartirse la línea en dos mitades. Con mitades, una
                    // etiqueta como "JUGADORES" no cabía en la suya y salía cortada aunque
                    // sobrara sitio de sobra al lado, porque el hueco del valor estaba vacío.
                    g.DrawString(cifra.Etiqueta(_textos), fEtiqueta, pincelEtiqueta,
                                 new RectangleF(hueco.X, hueco.Y + E(3), hueco.Width,
                                                fEtiqueta.GetHeight(g) + 2), izquierda);

                    using var pincelValor = new SolidBrush(cifra.Tono);
                    // A la misma altura que la etiqueta: con el mismo cuerpo, si uno arranca tres
                    // píxeles más arriba que el otro se nota que están descolocados.
                    g.DrawString(cifra.Ultimo, fValor, pincelValor,
                                 new RectangleF(hueco.X, hueco.Y + E(3), hueco.Width,
                                                fValor.GetHeight(g) + 2), derecha);
                    linea += altoLinea;
                }

                y = caja.Bottom + E(8);
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
        /// <summary>
        /// El fondo sin la marca de agua y volteado, para que Nox quede detrás del registro.
        ///
        /// El dibujo trae a Nox a la izquierda, que es justo donde va la columna de cifras: quedaba
        /// tapado. Volteado cae detrás del registro, que ahora es translúcido, y se le ve entero.
        ///
        /// El logotipo de Wakfu y la línea de copyright viven en la franja derecha del dibujo, y en
        /// espejo saldrían escritos del revés. El primer intento fue volver a pegar esa esquina sin
        /// voltear, y se notaba el recuadro a la legua: un trozo sin voltear dentro de una imagen
        /// volteada siempre deja costura. Así que la franja se RECORTA antes de voltear. Recortar y
        /// no pintar encima es lo que no deja artefactos: no hay nada que disimular, simplemente esa
        /// parte del dibujo no está.
        /// </summary>
        private static Image? Espejar(Image? foto)
        {
            if (foto == null) return null;

            try
            {
                // El 18% de la derecha cubre de sobra el logotipo y el copyright, y lo que se pierde
                // es el borde del pinar, que no lo echa nadie de menos.
                int ancho = Math.Max(1, (int)(foto.Width * 0.82f));
                var recorte = new Rectangle(0, 0, ancho, foto.Height);

                var espejo = new Bitmap(ancho, foto.Height);
                using (var g = Graphics.FromImage(espejo))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(foto, new Rectangle(0, 0, ancho, foto.Height), recorte, GraphicsUnit.Pixel);
                }

                espejo.RotateFlip(RotateFlipType.RotateNoneFlipX);
                foto.Dispose();
                return espejo;
            }
            catch
            {
                // Si algo falla, el fondo original sirve igual: se verá a Nox tapado por las
                // tarjetas, que es como estaba antes, pero la ventana abre.
                return foto;
            }
        }

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

        /// <summary>
        /// Cuánto ocupa la consola: poco más de la mitad de la ventana, no toda.
        ///
        /// Es una proporción y no un alto fijo para que quede igual de bien maximizada en un
        /// portátil que en un monitor grande. Y con un mínimo, para que estrechando la ventana no
        /// se quede en dos líneas.
        /// </summary>
        private void AjustarConsola()
        {
            // Puede llegar antes de tiempo: poner WindowState = Maximized en el constructor ya
            // dispara un OnResize, y en ese momento todavía no hay panel que ajustar. Sin esta
            // línea la ventana ni se abría -y el fallo salía como un escueto "Object reference
            // not set" en el registro-.
            if (_caja == null) return;

            // La consola ya no se dimensiona: va en Fill y se queda con lo que sobre. Lo único que
            // hay que decidir es cuánto se lleva la columna de cifras, y se le da lo justo para que
            // quepan sus dos números por línea sin comerle sitio al registro.
            int columna = Math.Max(E(260), Math.Min((int)(ClientSize.Width * 0.20f), E(420)));
            if (_cifras != null) _cifras.Width = columna;
        }

        /// <summary>
        /// El fondo detrás de la consola, y un marco alrededor.
        ///

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

            foreach (var (id, hora, texto) in nuevas)
            {
                if (id > _ultimaLinea) _ultimaLinea = id;
                Escribir(hora, texto);
            }

            // Ni recortar ni bajar el cursor: de las dos cosas se encarga la vista, que sabe cuántas
            // líneas guarda y si está pegada abajo.
            _registro.Follow = _seguir.Checked;
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

        /// <summary>
        /// Poner nombre al paquete que hay bajo el ratón.
        ///
        /// Sólo abre si el renglón es un paquete: sobre una línea normal del servidor no hay nada
        /// que bautizar y un menú que sale siempre acaba saliendo cuando no toca.
        /// </summary>
        private void Bautizar(Point donde)
        {
            var paquete = Paquete.Match(_registro.TextAt(donde));
            if (!paquete.Success) return;

            string opcode = paquete.Groups[3].Value;
            using var elegir = new NamePicker(opcode, Managers.NameBinding.Meaning(opcode),
                                              Managers.NameBinding.Of(opcode), _escala);

            if (elegir.ShowDialog(this) != DialogResult.OK) return;

            Managers.NameBinding.Bind(opcode, elegir.Chosen);
            Console.WriteLine(elegir.Chosen.Length > 0
                ? $"[Nombres] {opcode} es {elegir.Chosen}."
                : $"[Nombres] {opcode} vuelve a estar sin ligar.");
        }

        private void Escribir(string hora, string texto)
        {
            // Una entrada puede traer VARIOS renglones dentro.
            //
            // ConsoleLogBuffer guarda lo que le llega a Console.WriteLine tal cual, y hay sitios
            // que escriben varias líneas de una vez —el volcado de un paquete sin manejar, con sus
            // rayas y su árbol de campos—. El RichTextBox de antes las repartía solo; la vista
            // nueva dibuja cada entrada en UNA posición, así que un texto con saltos dentro salía
            // encaramado encima del siguiente y no se leía nada.
            //
            // La hora va sólo en el primero. Los demás llevan su hueco en blanco para que el texto
            // siga cuadrado en la misma columna.
            string[] renglones = texto.Replace("\r", "").Split('\n');
            string sangria = hora.Length > 0 ? new string(' ', hora.Length + 2) : "";

            for (int i = 0; i < renglones.Length; i++)
            {
                var trozos = new List<LauncherLogView.Piece>(7);
                if (hora.Length > 0)
                    trozos.Add(new(i == 0 ? hora + "  " : sangria, LauncherTheme.LogTime));

                var paquete = Paquete.Match(renglones[i]);
                if (paquete.Success) Paquete_(paquete, trozos);
                else trozos.Add(new(renglones[i], ColorDe(renglones[i])));

                _registro.Add(trozos.ToArray());
            }
        }

        /// <summary>
        /// El renglón de un paquete, cada trozo de su color.
        ///
        /// Los colores no son decorativos: el ojo busca el opcode, que va en claro, y el resto se
        /// aparta. La dirección lleva el mismo código que el resto del emulador —azul lo que baja
        /// del servidor, dorado lo que sube del cliente— para no tener que leerla.
        /// </summary>
        private static void Paquete_(System.Text.RegularExpressions.Match m,
                                     List<LauncherLogView.Piece> trozos)
        {
            void Trozo(string texto, Color color)
            {
                if (texto.Length > 0) trozos.Add(new LauncherLogView.Piece(texto, color));
            }

            string direccion = m.Groups[2].Value;

            Trozo(m.Groups[1].Value + " ", LauncherTheme.LogTime);
            Trozo(direccion + " ", direccion.Contains("server>") ? LauncherTheme.LogZaap : LauncherTheme.LogServer);
            Trozo(m.Groups[3].Value, LauncherTheme.HighlightText);
            Trozo(m.Groups[4].Value, LauncherTheme.LogNormal);
            Trozo(m.Groups[5].Value, LauncherTheme.LightBrownText);
            Trozo(m.Groups[6].Value, LauncherTheme.LogTime);
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
                Font = LauncherTheme.CreateFont(11f * _escala, FontStyle.Bold),
                Height = E(30),
                Width = ancho > 0 ? ancho : E(120),
                LetterSpacing = 1f,
                CornerRadius = E(5),
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
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    try { Application.SetHighDpiMode(HighDpiMode.SystemAware); } catch { }
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
