using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Jondo.Unity.Launcher.UI.Widgets;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// La ventana del lanzador.
    /// </summary>
    /// <remarks>
    /// Es la misma interfaz de siempre —la tarjeta de acceso sobre el dibujo del fondo, con su
    /// música— reescrita en Avalonia. Lo que se ve no cambia; lo que cambia es que ya no hay que
    /// pintarlo a mano:
    ///
    ///   - la colocación la hacen StackPanel y Grid, y con ella se van los 250 lines de LayOutCard
    ///     y su Px() multiplicando cada medida por el DPI
    ///   - la transparencia la compone Avalonia, y con ella se va el bitmap de fondo compartido
    ///   - el fondo recortado a lo «background-size: cover» es Stretch="UniformToFill"
    ///
    /// Lo que sí es distinto de verdad son dos cosas, y las dos a mejor. La primera: entrar y
    /// registrarse ya no bloquean la ventana; van por <see cref="Task.Run(Action)"/> y mientras
    /// tanto los botones se apagan. Antes la llamada HTTP se hacía en el hilo de la interfaz con un
    /// cursor de reloj, y un servidor lento dejaba la ventana congelada. La segunda: las cuentas
    /// guardadas van cifradas, ver <see cref="Security.SecretStore"/>.
    /// </remarks>
    public sealed partial class MainWindow : Window
    {
        private sealed class TeamAccount
        {
            public long AccountId { get; init; }
            public string Login { get; set; } = "";
            public string Token { get; set; } = "";
            public string Nickname { get; set; } = "";
            public string RefreshToken { get; set; } = "";
            public long ExpiresAtUnix { get; set; }
            public bool Selected { get; set; }

            /// <summary>El personaje que se enseña en la fila, cuando el servidor ya lo ha dicho.</summary>
            public LauncherService.Character? Personaje { get; set; }

            /// <summary>Su retrato, dibujado con los huesos del cliente. Null mientras no haya.</summary>
            public Avalonia.Media.Imaging.Bitmap? Retrato { get; set; }
        }

        private readonly List<TeamAccount> _cuentas = new();
        private readonly DispatcherTimer _reloj = new() { Interval = TimeSpan.FromSeconds(2) };

        /// <summary>Las tres pantallas del lanzador.</summary>
        /// <remarks>
        /// Antes no había ninguna: entrar, el equipo, el idioma, la música, la ruta del cliente y
        /// el estado del servidor estaban apilados en la misma columna de 350 píxeles, y la
        /// pantalla que tocaba se decidía con un booleano de «he entrado o no». Eso deja los
        /// ajustes —cosas que se tocan una vez— compitiendo por sitio con el botón de jugar.
        /// </remarks>
        private enum Seccion { Jugar, Cuentas, Ajustes }

        private Seccion _seccion = Seccion.Jugar;
        private bool _servidorEnLinea;
        private bool _modoRegistro;
        private bool _ocupado;
        private Language _idioma;
        private LauncherTexts _textos;
        private MusicPlayer? _musica;
        private string _firmaDeActivas = "";

        /// <summary>Quien dibuja los retratos, sacándolos de los huesos del cliente de Dofus.</summary>
        /// <remarks>
        /// Es el mismo que usa Studio para los NPC de los mapas. Guarda dentro lo que ya ha
        /// dibujado, así que uno por ventana y no uno por fila. Se cierra al cerrarse la ventana.
        /// </remarks>
        private readonly Jondo.Unity.Sprites.NpcSprites _retratos = new()
        {
            // Cuatro veces el hueco. El dibujante no suaviza nada —una muestra por píxel— así que
            // a la altura de la ficha los bordes salían de sierra; dibujando grande y dejando que
            // Avalonia lo reduzca, la reducción hace de suavizado. La dirección la pone él solo:
            // de frente, que es lo que se pide de un retrato.
            Height = 256,
        };

        public MainWindow()
        {
            _idioma = LauncherPreferences.Language;
            _textos = LauncherTexts.Get(_idioma);

            InitializeComponent();

            Fondo.Source = LauncherSkin.LoadImage("bg.jpg");
            _reloj.Tick += (_, _) => MirarElEstado();
        }

        /// <summary>
        /// Recupera las cuentas de la vez anterior y comprueba con el servidor si su sesión vale.
        /// </summary>
        /// <remarks>
        /// Esto estaba en el constructor y ahí hacía daño: son hasta ocho peticiones HTTP, una por
        /// cuenta, y la ventana no se dibujaba hasta que terminaban todas. Con el servidor apagado
        /// eran ocho tiempos de espera seguidos —media docena de segundos largos— con la pantalla
        /// en negro y sin nada que dijera que el lanzador estaba vivo.
        ///
        /// Ahora la ventana sale primero y las cuentas aparecen cuando el servidor conteste. Lo que
        /// no cambia es lo que se comprueba: sin preguntar, se daba por dentro a cualquiera que
        /// tuviera cuenta guardada aunque el servidor hubiera rechazado su credencial, y quedaba
        /// una ventana que decía estar dentro con un botón de jugar que fallaba.
        /// </remarks>
        private async Task CargarLasCuentasAsync()
        {
            var guardadas = await Task.Run(() => LauncherPreferences.LoadAccounts());
            if (guardadas.Count == 0) return;

            var vivas = await Task.Run(() =>
            {
                var cuales = new HashSet<long>();
                foreach (var cuenta in guardadas)
                {
                    if (LauncherService.RememberSession(cuenta.AccountId, cuenta.Token))
                    {
                        cuales.Add(cuenta.AccountId);
                    }
                }
                return cuales;
            });

            foreach (var guardada in guardadas)
            {
                _cuentas.Add(new TeamAccount
                {
                    AccountId = guardada.AccountId,
                    Login = guardada.Login,
                    Nickname = guardada.Nickname,
                    Token = guardada.Token,
                    RefreshToken = guardada.RefreshToken,
                    ExpiresAtUnix = guardada.ExpiresAtUnix,
                    Selected = guardada.Selected,
                });
            }

            // Si el servidor ha rechazado TODAS las sesiones guardadas, hay que volver a entrar:
            // se enseña la sección de cuentas en vez de un equipo con un botón que fallaría.
            if (vivas.Count == 0)
            {
                _seccion = Seccion.Cuentas;
                Avisar(_textos.SessionExpiredError);
            }

            RefrescarResumen();
            Recolocar();

            // Y los retratos al final del todo, que es lo que más tarda y lo que menos falta hace
            // para poder jugar.
            await CargarLosRetratosAsync();
        }

        /// <summary>Mete cuentas inventadas para poder fotografiar la pantalla de jugar.</summary>
        /// <remarks>
        /// Sólo lo usa el harness de capturas. Sin esto, una ventana recién abierta sin cuentas
        /// guardadas enseña el estado vacío, que es justo la pantalla que NO hay que mirar al
        /// trabajar el diseño del equipo.
        /// </remarks>
        internal void MeterCuentasDeMentira(int cuantas)
        {
            _cuentas.Clear();
            for (int i = 1; i <= cuantas; i++)
            {
                _cuentas.Add(new TeamAccount
                {
                    AccountId = 188940900 + i,
                    Login = "cuenta" + i,
                    Nickname = i == 1 ? "Keka" : "Cuenta " + i,
                    Token = "de mentira",
                    Selected = i <= 2,
                });
            }

            _seccion = Seccion.Jugar;
            RefrescarResumen();
            Recolocar();
        }

        /// <summary>Pide los personajes al servidor y les dibuja el retrato.</summary>
        /// <remarks>
        /// Dos cosas separadas y en dos sitios distintos a propósito:
        ///
        ///   - <b>QUÉ dibujar</b> lo dice el servidor. La cadena de aspecto está en la base de
        ///     datos, y el lanzador no la toca: es lo que se reparte a los jugadores y sólo lleva
        ///     el contrato. Eso es una petición por cuenta, y va fuera del hilo de la interfaz.
        ///
        ///   - <b>DIBUJARLO</b> se hace aquí, con los huesos del cliente de Dofus, igual que hace
        ///     Studio con los NPC. No llevamos ni un retrato dentro del ejecutable.
        ///
        /// Va al final del todo y sin bloquear nada: la ventana ya está en pantalla y el equipo ya
        /// se puede usar. Los retratos aparecen cuando aparezcan, y si el cliente no está donde se
        /// cree, no aparecen y la ficha se lee igual.
        /// </remarks>
        private async Task CargarLosRetratosAsync()
        {
            foreach (var cuenta in _cuentas)
            {
                string token = cuenta.Token;
                var personajes = await Task.Run(() => LauncherService.CharactersOf(token));
                if (personajes.Count == 0) continue;

                // El de más nivel: es el que la gente reconoce como «su» personaje.
                personajes.Sort((a, b) => b.Level.CompareTo(a.Level));
                cuenta.Personaje = personajes[0];

                try
                {
                    cuenta.Retrato = _retratos.Of(personajes[0].Look);
                }
                catch (Exception ex)
                {
                    // Un aspecto que no se sepa dibujar no puede dejar el lanzador sin equipo.
                    Program.LogDebug($"[Lanzador] Sin retrato para {personajes[0].Name}: {ex.Message}");
                }
            }

            RefrescarFilasDeCuentas();
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Vida de la ventana
        // ═══════════════════════════════════════════════════════════════════════

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            AplicarIdioma();
            Recolocar();

            // La música arranca sola, igual que el autoplay de la interfaz web.
            if (OperatingSystem.IsWindows())
            {
                _musica = new MusicPlayer(Path.Combine(LauncherSkin.AssetsFolder, "theme.mp3"));
                if (_musica.Available) _musica.Play();
            }
            RefrescarBotonDeMusica();

            MirarElEstado();
            _reloj.Start();

            AlFrente();

            // Y las cuentas guardadas, ya con la ventana en pantalla.
            _ = CargarLasCuentasAsync();
        }

        /// <summary>
        /// Pone la ventana delante al abrirse.
        /// </summary>
        /// <remarks>
        /// Windows NO le da el primer plano a una ventana creada por un proceso que no era el
        /// activo, así que un <c>Activate()</c> a secas no basta: la ventana se abre DETRÁS de lo
        /// que hubiera en pantalla y la única señal de que el lanzador arrancó es la música.
        /// Marcarla como «siempre encima» un instante es lo que se salta esa regla; se le quita
        /// justo después para que a partir de ahí se comporte como cualquier otra.
        ///
        /// La versión de Windows Forms tenía esto mismo y al migrar a Avalonia se quedó en un
        /// Activate() suelto. El resultado era un lanzador que «no arranca»: arrancaba, abría su
        /// ventana, y se quedaba debajo del navegador.
        /// </remarks>
        private void AlFrente()
        {
            try
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Maximized;

                Topmost = true;
                Activate();
                Topmost = false;
                Focus();
            }
            catch
            {
                // No poder ponerse delante no es motivo para fallar: la ventana ya está ahí,
                // sólo que debajo.
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _reloj.Stop();
            _musica?.Dispose();
            _retratos.Dispose();
            base.OnClosed(e);

            // Cerrar la ventana ya NO apaga el emulador: sólo termina este proceso. El servidor es
            // otro programa y sigue con los jugadores que tenga dentro.
            Program.RequestShutdown("se ha cerrado la ventana del lanzador");
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Qué se ve en cada momento
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>Enseña la sección que toque y apaga las otras dos.</summary>
        private void Recolocar()
        {
            PanelJugar.IsVisible = _seccion == Seccion.Jugar;
            PanelCuentas.IsVisible = _seccion == Seccion.Cuentas;
            PanelAjustes.IsVisible = _seccion == Seccion.Ajustes;

            SeccionJugar.Classes.Set("on", _seccion == Seccion.Jugar);
            SeccionCuentas.Classes.Set("on", _seccion == Seccion.Cuentas);
            SeccionAjustes.Classes.Set("on", _seccion == Seccion.Ajustes);

            FormularioEntrar.IsVisible = !_modoRegistro;
            FormularioRegistro.IsVisible = _modoRegistro;
            PestanaEntrar.Classes.Set("on", !_modoRegistro);
            PestanaRegistro.Classes.Set("on", _modoRegistro);

            // Sin cuentas todavía, la pantalla de jugar no enseña una lista vacía y un botón que
            // no hace nada: enseña qué hay que hacer y el atajo para hacerlo.
            bool hayCuentas = _cuentas.Count > 0;
            ListaDeCuentas.IsVisible = hayCuentas;
            EquipoVacio.IsVisible = !hayCuentas;
            BotonJugar.IsVisible = hayCuentas;
            BotonTodas.IsVisible = hayCuentas;

            // Y con el equipo lleno se apaga en vez de esconderse, con el motivo puesto encima:
            // un botón que desaparece deja pensando si alguna vez estuvo.
            bool cabenMas = _cuentas.Count < LauncherService.MaximumClients;
            AnadirOtra.IsVisible = hayCuentas;
            AnadirOtra.IsEnabled = cabenMas;
            AnadirOtra.Opacity = cabenMas ? 1 : 0.45;
            ToolTip.SetTip(AnadirOtra, cabenMas ? null : _textos.MaxAccountsError);

            if (hayCuentas) RefrescarFilasDeCuentas();
        }

        private void AlPulsarSeccion(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
        {
            QuitarElAviso();
            _seccion = (remitente as Button)?.Tag as string switch
            {
                "cuentas" => Seccion.Cuentas,
                "ajustes" => Seccion.Ajustes,
                _ => Seccion.Jugar,
            };
            Recolocar();
        }

        private void AlPulsarIrACuentas(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _seccion = Seccion.Cuentas;
            _modoRegistro = false;
            Recolocar();
        }

        private void AplicarIdioma()
        {
            BotonEs.Classes.Set("on", _idioma == Language.Es);
            BotonEn.Classes.Set("on", _idioma == Language.En);
            BotonFr.Classes.Set("on", _idioma == Language.Fr);

            PestanaEntrar.Content = Etiqueta(_textos.LoginTab, 1, bold: true);
            PestanaRegistro.Content = Etiqueta(_textos.RegisterTab, 1, bold: true);

            RotuloUsuario.Text = _textos.UsernameLabel.ToUpperInvariant();
            RotuloClave.Text = _textos.PasswordLabel.ToUpperInvariant();
            RotuloNuevoUsuario.Text = _textos.NewUsernameLabel.ToUpperInvariant();
            RotuloNuevaClave.Text = _textos.NewPasswordLabel.ToUpperInvariant();
            RotuloApodo.Text = _textos.NicknameLabel.ToUpperInvariant();

            CampoUsuario.Watermark = _textos.UsernamePlaceholder;
            CampoClave.Watermark = "••••••••";
            CampoNuevoUsuario.Watermark = _textos.NewUsernamePlaceholder;
            CampoNuevaClave.Watermark = "••••••••";
            CampoApodo.Watermark = _textos.NicknamePlaceholder;

            TextoConectar.Text = _textos.ConnectButton;
            TextoCrear.Text = _textos.CreateButton;
            TextoQuitarCuentas.Text = _textos.RemoveSelected;

            // Los rótulos de las tres secciones y los de los ajustes. Van aquí y no en el XAML
            // porque cambian con el idioma como todo lo demás.
            SeccionJugar.Content = Etiqueta(Textos.Jugar(_idioma), 1.5, bold: true);
            SeccionCuentas.Content = Etiqueta(Textos.Cuentas(_idioma), 1.5, bold: true);
            SeccionAjustes.Content = Etiqueta(Textos.Ajustes(_idioma), 1.5, bold: true);

            RotuloIdioma.Text = Textos.Idioma(_idioma).ToUpperInvariant();
            RotuloMusica.Text = Textos.Musica(_idioma).ToUpperInvariant();
            RotuloCliente.Text = Textos.Cliente(_idioma).ToUpperInvariant();
            RotuloCuentasGuardadas.Text = Textos.CuentasGuardadas(_idioma).ToUpperInvariant();

            PieIdioma.Text = Textos.PieIdioma(_idioma);
            PieCliente.Text = Textos.PieCliente(_idioma);
            PieCuentasGuardadas.Text = Textos.PieCuentasGuardadas(_idioma);

            TextoEquipoVacio.Text = Textos.EquipoVacio(_idioma);
            TextoPrimeraCuenta.Text = _textos.AddAccountButton;
            TextoAnadirOtra.Text = _textos.AddAccountButton;

            // El idioma manda también sobre el juego: es el --langCode con el que arranca. Por eso
            // la fila de la ruta lo enseña, para no tener que adivinar en qué idioma va a abrir.
            RefrescarRutaDelCliente();
            RefrescarResumen();
            RefrescarBotonDeMusica();
            RefrescarEstado();
        }

        /// <summary>El rótulo espaciado de las pestañas, que en la web era el letter-spacing.</summary>
        private SpacedText Etiqueta(string texto, double separacion, bool bold) => new SpacedText
        {
            Text = texto,
            Spacing = separacion,
            Shadow = true,
            FontSize = 12,
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
            FontFamily = LauncherSkin.Title,
            Foreground = new SolidColorBrush(LauncherSkin.CardText),
        };

        private void RefrescarRutaDelCliente()
        {
            string ruta = LauncherService.ResolveClient();
            string guardada = LauncherPreferences.ClientExecutableRaw;
            string idioma = LauncherTexts.Code(_idioma).ToUpperInvariant();

            if (ruta.Length == 0)
            {
                TextoRuta.Text = guardada.Length > 0
                    ? "Dofus.exe ya no está donde se dejó — elige dónde está"
                    : "No se encuentra Dofus.exe — elige dónde está";
                BotonRuta.Foreground = new SolidColorBrush(Color.FromRgb(236, 120, 96));
            }
            else
            {
                TextoRuta.Text = Recortar(ruta) + "   ·   " + idioma;
                BotonRuta.Foreground = new SolidColorBrush(LauncherSkin.Gold);
            }
        }

        /// <summary>Rutas largas por el medio: el final es lo que identifica al fichero.</summary>
        private static string Recortar(string ruta)
        {
            const int tope = 46;
            if (ruta.Length <= tope) return ruta;
            return ruta.Substring(0, 12) + "…" + ruta.Substring(ruta.Length - (tope - 13));
        }

        private void RefrescarResumen()
        {
            int elegidas = _cuentas.Count(a => a.Selected);

            TituloEquipo.Text = string.Format(_textos.TeamTitle, _cuentas.Count);
            // «2 seleccionado(s) · 0 activo(s)» no decía qué era «activo». Ahora lo dice.
            ResumenEquipo.Text = Textos.Resumen(_idioma, elegidas, LauncherService.ActiveCount);
            TextoJugar.Text = string.Format(_textos.LaunchSelected, elegidas);
            TextoTodas.Text = elegidas == _cuentas.Count && elegidas > 0
                ? _textos.DeselectAll
                : _textos.SelectAll;

            BotonJugar.IsEnabled = _servidorEnLinea && elegidas > 0 && !_ocupado;
        }

        private void RefrescarFilasDeCuentas()
        {
            FilasDeCuentas.Children.Clear();

            foreach (var cuenta in _cuentas)
            {
                var propia = cuenta;
                bool jugando = LauncherService.IsActive(cuenta.AccountId);

                var fila = new Button { Classes = { "account" } };
                fila.Classes.Set("on", cuenta.Selected);
                fila.Content = FichaDe(cuenta, jugando);
                fila.Click += (_, _) =>
                {
                    propia.Selected = !propia.Selected;
                    GuardarCuentas();
                    RefrescarResumen();
                    RefrescarFilasDeCuentas();
                };
                FilasDeCuentas.Children.Add(fila);
            }

            _firmaDeActivas = FirmaDeActivas();
        }

        /// <summary>La cajita de una cuenta: retrato, nombre, nivel y la marca de si va.</summary>
        /// <remarks>
        /// Era una línea de texto con un cuadradito y el apodo. Ahora es una ficha con el
        /// personaje dibujado, porque en un equipo de ocho lo que se reconoce de un vistazo es la
        /// cara, no el número de cuenta.
        ///
        /// El retrato sale de los huesos del CLIENTE, no de ningún dibujo que llevemos nosotros
        /// dentro: el servidor dice la cadena de aspecto y <see cref="Jondo.Unity.Sprites.NpcSprites"/>
        /// la pinta. Si el cliente no está donde se cree, o esa cadena no se sabe dibujar, el hueco
        /// se queda con la inicial del nombre y la ficha sigue leyéndose igual.
        /// </remarks>
        private Control FichaDe(TeamAccount cuenta, bool jugando)
        {
            var marca = new TextBlock
            {
                Text = cuenta.Selected ? "✔" : "",
                FontSize = 15,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(LauncherSkin.LightGold),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };

            var casilla = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new Avalonia.CornerRadius(4),
                BorderThickness = new Avalonia.Thickness(2),
                BorderBrush = new SolidColorBrush(cuenta.Selected ? LauncherSkin.LightGold : LauncherSkin.BorderBrown),
                Background = new SolidColorBrush(cuenta.Selected ? LauncherSkin.LightBrown : Color.FromArgb(90, 0, 0, 0)),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Child = marca,
            };

            // Los retratos salen de unos 75 por 96, así que el hueco va con esa proporción y el
            // personaje entra ENTERO. Con la caja más cuadrada y alineado abajo se le veían las
            // piernas y poco más.
            var hueco = new Border
            {
                Width = 50,
                Height = 64,
                CornerRadius = new Avalonia.CornerRadius(5),
                Background = new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
                ClipToBounds = true,
                Child = cuenta.Retrato != null
                    ? new Image
                    {
                        Source = cuenta.Retrato,
                        Stretch = Avalonia.Media.Stretch.Uniform,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    }
                    : (Control)new TextBlock
                    {
                        Text = Inicial(cuenta),
                        FontSize = 20,
                        Foreground = new SolidColorBrush(LauncherSkin.BorderBrown),
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    },
            };

            string nombre = cuenta.Personaje?.Name is { Length: > 0 } suyo ? suyo : cuenta.Nickname;

            var letras = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = nombre,
                        FontSize = 13.5,
                        FontWeight = FontWeight.Bold,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                    new TextBlock
                    {
                        Text = cuenta.Personaje != null
                            ? $"{Textos.Nivel(_idioma)} {cuenta.Personaje.Level}  ·  #{cuenta.AccountId}"
                            : $"#{cuenta.AccountId}",
                        FontSize = 10.5,
                        Foreground = new SolidColorBrush(LauncherSkin.MutedGold),
                    },
                },
            };

            var dentro = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
            Grid.SetColumn(casilla, 0);
            Grid.SetColumn(hueco, 1);
            Grid.SetColumn(letras, 2);
            hueco.Margin = new Avalonia.Thickness(10, 0, 10, 0);

            dentro.Children.Add(casilla);
            dentro.Children.Add(hueco);
            dentro.Children.Add(letras);

            if (jugando)
            {
                var enJuego = new Border
                {
                    CornerRadius = new Avalonia.CornerRadius(10),
                    Padding = new Avalonia.Thickness(8, 3),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Background = new SolidColorBrush(Color.FromArgb(70, 80, 200, 80)),
                    Child = new TextBlock
                    {
                        Text = _textos.InGame,
                        FontSize = 9.5,
                        FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(LauncherSkin.OnlineGreen),
                    },
                };
                Grid.SetColumn(enJuego, 3);
                dentro.Children.Add(enJuego);
            }

            return dentro;
        }

        /// <summary>La inicial que se enseña mientras no hay retrato.</summary>
        private static string Inicial(TeamAccount cuenta)
        {
            string de = cuenta.Personaje?.Name is { Length: > 0 } suyo ? suyo : cuenta.Nickname;
            return de.Length > 0 ? de.Substring(0, 1).ToUpperInvariant() : "?";
        }

        private string FirmaDeActivas()
            => string.Join(",", _cuentas.Where(a => LauncherService.IsActive(a.AccountId))
                                        .Select(a => a.AccountId));

        private void RefrescarEstado()
        {
            PuntoEstado.Online = _servidorEnLinea;
            TextoEstado.Text = _servidorEnLinea ? _textos.StatusOnline : _textos.StatusOffline;
            TextoEstado.Foreground = new SolidColorBrush(
                _servidorEnLinea ? LauncherSkin.OnlineGreen : LauncherSkin.Red);
        }

        private void RefrescarBotonDeMusica()
        {
            // Decía «MÚSICA: ON», que es su estado y no lo que pasa al pulsarlo. Un botón se
            // nombra por lo que hace.
            bool sonando = _musica?.Playing ?? false;
            TextoMusica.Text = sonando ? Textos.ApagarMusica(_idioma) : Textos.EncenderMusica(_idioma);
            IconoAltavoz.Opacity = sonando ? 1 : 0.45;
            BotonMusica.Classes.Set("on", sonando);
        }

        private void Avisar(string mensaje)
        {
            TextoAviso.Text = string.IsNullOrWhiteSpace(mensaje) ? _textos.GenericError : mensaje;
            Aviso.IsVisible = true;
        }

        private void QuitarElAviso() => Aviso.IsVisible = false;

        /// <summary>
        /// Apaga los campos mientras no se pueda usarlos.
        /// </summary>
        /// <remarks>
        /// Dos motivos y no uno: que el servidor no conteste, y que haya una petición en marcha.
        /// El segundo no estaba y por eso se podía pulsar «entrar» dos veces y mandar dos peticiones.
        /// </remarks>
        private void HabilitarCampos()
        {
            bool se_puede = _servidorEnLinea && !_ocupado;

            CampoUsuario.IsEnabled = se_puede;
            CampoClave.IsEnabled = se_puede;
            CampoNuevoUsuario.IsEnabled = se_puede;
            CampoNuevaClave.IsEnabled = se_puede;
            CampoApodo.IsEnabled = se_puede;
            BotonConectar.IsEnabled = se_puede;
            BotonCrear.IsEnabled = se_puede;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  El pulso
        // ═══════════════════════════════════════════════════════════════════════

        private void MirarElEstado()
        {
            bool enLinea;
            try
            {
                enLinea = LauncherService.GetStatus().Online;
            }
            catch
            {
                enLinea = false;
            }

            if (enLinea != _servidorEnLinea)
            {
                _servidorEnLinea = enLinea;
                HabilitarCampos();
            }

            RefrescarEstado();
            _musica?.KeepLooping();

            if (_cuentas.Count > 0)
            {
                RefrescarResumen();
                if (FirmaDeActivas() != _firmaDeActivas) RefrescarFilasDeCuentas();
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Lo que se puede pulsar
        // ═══════════════════════════════════════════════════════════════════════

        private void AlPulsarMusica(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_musica == null || !_musica.Available) return;

            if (_musica.Playing) _musica.Pause();
            else _musica.Play();

            RefrescarBotonDeMusica();
        }

        private void AlPulsarIdioma(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
        {
            string codigo = (remitente as Button)?.Tag as string ?? "es";
            Language nuevo = codigo switch
            {
                "en" => Language.En,
                "fr" => Language.Fr,
                _ => Language.Es,
            };

            if (_idioma == nuevo) return;

            _idioma = nuevo;
            _textos = LauncherTexts.Get(nuevo);
            LauncherPreferences.Language = nuevo;
            AplicarIdioma();
            Recolocar();
        }

        private void AlPulsarPestanaEntrar(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
            => CambiarDePestana(false);

        private void AlPulsarPestanaRegistro(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
            => CambiarDePestana(true);

        private void CambiarDePestana(bool registro)
        {
            QuitarElAviso();
            _modoRegistro = registro;
            Recolocar();
        }

        private void AlPulsarTeclaEnEntrar(object? remitente, KeyEventArgs e)
        {
            if (e.Key == Key.Return || e.Key == Key.Enter) _ = EntrarAsync();
        }

        private void AlPulsarTeclaEnRegistro(object? remitente, KeyEventArgs e)
        {
            if (e.Key == Key.Return || e.Key == Key.Enter) _ = RegistrarAsync();
        }

        private void AlPulsarConectar(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
            => _ = EntrarAsync();

        private void AlPulsarCrear(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
            => _ = RegistrarAsync();

        private void AlPulsarJugar(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
            => _ = JugarAsync();

        private void AlPulsarQuitarCuentas(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
        {
            int quitadas = _cuentas.RemoveAll(a => a.Selected && !LauncherService.IsActive(a.AccountId));
            if (quitadas == 0)
            {
                Avisar(_textos.SelectAccountError);
                return;
            }

            GuardarCuentas();
            RefrescarResumen();
            Recolocar();
        }

        private void AlPulsarTodas(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
        {
            bool marcar = !_cuentas.All(a => a.Selected);
            foreach (var cuenta in _cuentas) cuenta.Selected = marcar;

            GuardarCuentas();
            RefrescarResumen();
            RefrescarFilasDeCuentas();
        }

        private async void AlPulsarRuta(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var opciones = new FilePickerOpenOptions
            {
                Title = "¿Dónde está el cliente de Dofus?",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Dofus.exe") { Patterns = new[] { "Dofus.exe" } },
                    new FilePickerFileType("Ejecutables") { Patterns = new[] { "*.exe" } },
                },
            };

            string actual = LauncherService.ResolveClient();
            if (actual.Length > 0)
            {
                string? carpeta = Path.GetDirectoryName(actual);
                if (carpeta != null)
                {
                    try
                    {
                        opciones.SuggestedStartLocation =
                            await StorageProvider.TryGetFolderFromPathAsync(carpeta);
                    }
                    catch
                    {
                        // Que no se pueda sugerir carpeta no impide elegir fichero.
                    }
                }
            }

            var elegidos = await StorageProvider.OpenFilePickerAsync(opciones);
            string? ruta = elegidos.Count > 0 ? elegidos[0].TryGetLocalPath() : null;
            if (string.IsNullOrEmpty(ruta)) return;

            LauncherPreferences.ClientExecutable = ruta;
            RefrescarRutaDelCliente();
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Entrar, registrarse y jugar
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Entra: por la web si la hay, y si no con usuario y contraseña.
        /// </summary>
        /// <remarks>
        /// El día que exista la web, <see cref="LauncherPreferences.WebSite"/> deja de estar vacío
        /// y este método empieza a abrir el navegador sin que haya que tocar nada más. Ver
        /// <see cref="Security.OAuthFlow"/>.
        /// </remarks>
        private async Task EntrarAsync()
        {
            if (!_servidorEnLinea || _ocupado) return;
            QuitarElAviso();

            if (LauncherPreferences.HasWebSite)
            {
                await EntrarPorLaWebAsync();
                return;
            }

            string usuario = CampoUsuario.Text?.Trim() ?? "";
            string clave = CampoClave.Text?.Trim() ?? "";

            Ocupado(true);
            LauncherService.SignInResult resultado;
            try
            {
                // Fuera del hilo de la interfaz: es una petición de red y antes congelaba la
                // ventana mientras durase.
                resultado = await Task.Run(() =>
                    LauncherService.SignIn(usuario, clave, LauncherService.LocalIp));
            }
            catch (Exception ex)
            {
                Avisar(ex.Message);
                return;
            }
            finally
            {
                Ocupado(false);
            }

            if (!resultado.Success)
            {
                Avisar(resultado.Message);
                return;
            }

            CampoUsuario.Text = "";
            CampoClave.Text = "";

            AnadirAlEquipo(resultado.AccountId, usuario,
                           string.IsNullOrEmpty(resultado.Nickname) ? usuario : resultado.Nickname,
                           resultado.Token, "", 0);
        }

        private async Task EntrarPorLaWebAsync()
        {
            Ocupado(true);
            try
            {
                var puntos = Security.OAuthFlow.Endpoints.For(LauncherPreferences.WebSite);
                var sesion = await Security.OAuthFlow.SignInAsync(puntos);

                // La web devuelve un vale; el servidor de juego es quien dice a qué cuenta
                // corresponde. Con eso ya se puede montar la ficha del equipo.
                var quien = await Task.Run(() => LauncherService.SignInWithToken(sesion.AccessToken));
                if (!quien.Success)
                {
                    Avisar(quien.Message);
                    return;
                }

                AnadirAlEquipo(quien.AccountId, quien.Nickname, quien.Nickname, sesion.AccessToken,
                               sesion.RefreshToken, sesion.ExpiresAt.ToUnixTimeSeconds());
            }
            catch (Security.OAuthFlow.OAuthException ex)
            {
                Avisar(ex.Message);
            }
            catch (Exception ex)
            {
                Avisar(_textos.GenericError + "\n" + ex.Message);
            }
            finally
            {
                Ocupado(false);
            }
        }

        private void AnadirAlEquipo(long id, string login, string apodo, string token,
                                    string refresco, long caduca)
        {
            int yaEsta = _cuentas.FindIndex(a => a.AccountId == id);
            if (yaEsta < 0 && _cuentas.Count >= LauncherService.MaximumClients)
            {
                Avisar(_textos.MaxAccountsError);
                return;
            }

            var ficha = new TeamAccount
            {
                AccountId = id,
                Login = login,
                Nickname = apodo,
                Token = token,
                RefreshToken = refresco,
                ExpiresAtUnix = caduca,
                Selected = true,
            };

            if (yaEsta >= 0)
            {
                ficha.Selected = _cuentas[yaEsta].Selected;
                _cuentas[yaEsta] = ficha;
            }
            else _cuentas.Add(ficha);

            // Recién entrado, lo que quiere es jugar.
            _seccion = Seccion.Jugar;
            GuardarCuentas();
            RefrescarResumen();
            Recolocar();
        }

        private async Task RegistrarAsync()
        {
            if (!_servidorEnLinea || _ocupado) return;
            QuitarElAviso();

            string usuario = CampoNuevoUsuario.Text?.Trim() ?? "";
            string clave = CampoNuevaClave.Text?.Trim() ?? "";
            string apodo = CampoApodo.Text?.Trim() ?? "";

            Ocupado(true);
            LauncherService.Result resultado;
            try
            {
                resultado = await Task.Run(() =>
                    LauncherService.RegisterAccount(usuario, clave, apodo, LauncherService.LocalIp));
            }
            finally
            {
                Ocupado(false);
            }

            if (!resultado.Success)
            {
                Avisar(resultado.Message);
                return;
            }

            CampoNuevoUsuario.Text = "";
            CampoNuevaClave.Text = "";
            CampoApodo.Text = "";

            await Dialogs.ShowAsync(this, Title ?? "Jondo", _textos.AccountCreatedMessage,
                                    _textos.DialogAccept);
            CambiarDePestana(false);
        }

        private async Task JugarAsync()
        {
            var elegidas = _cuentas.Where(a => a.Selected).ToList();
            if (elegidas.Count == 0)
            {
                Avisar(_textos.SelectAccountError);
                return;
            }

            Ocupado(true);
            List<string> fallos;
            try
            {
                fallos = await Task.Run(() =>
                {
                    var malos = new List<string>();
                    foreach (var cuenta in elegidas)
                    {
                        if (LauncherService.IsActive(cuenta.AccountId)) continue;
                        var resultado = LauncherService.LaunchClient(cuenta.Token);
                        if (!resultado.Success) malos.Add(cuenta.Nickname + " : " + resultado.Message);
                    }
                    return malos;
                });
            }
            finally
            {
                Ocupado(false);
            }

            if (fallos.Count > 0) Avisar(string.Join(Environment.NewLine, fallos));

            // Arrancar el cliente calla la música del lanzador.
            if (_musica != null && _musica.Playing)
            {
                _musica.Stop();
                RefrescarBotonDeMusica();
            }

            RefrescarResumen();
            RefrescarFilasDeCuentas();

            // Sin ventana de confirmación: que se abra el cliente ya lo confirma, y un diálogo
            // modal se quedaría encima del juego esperando a que alguien lo cierre.
        }

        private void Ocupado(bool si)
        {
            _ocupado = si;
            Cursor = new Cursor(si ? StandardCursorType.Wait : StandardCursorType.Arrow);
            HabilitarCampos();
            RefrescarResumen();
        }

        private void GuardarCuentas()
            => LauncherPreferences.SaveAccounts(_cuentas.Select(a => new LauncherPreferences.SavedAccount
            {
                AccountId = a.AccountId,
                Login = a.Login,
                Nickname = a.Nickname,
                Token = a.Token,
                RefreshToken = a.RefreshToken,
                ExpiresAtUnix = a.ExpiresAtUnix,
                Selected = a.Selected,
            }));
    }
}
