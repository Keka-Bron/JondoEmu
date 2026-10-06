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
    /// The launcher's window.
    /// </summary>
    /// <remarks>
    /// It is the same interface as always —the login card over the background drawing, with its
    /// music— rewritten in Avalonia. What is seen does not change; what changes is that it no longer has to be
    /// painted by hand:
    ///
    ///   - layout is done by StackPanel and Grid, and with it go the 250 lines of LayOutCard
    ///     and its Px() multiplying each measure by the DPI
    ///   - the transparency is composed by Avalonia, and with it goes the shared background bitmap
    ///   - the background cropped «background-size: cover» style is Stretch="UniformToFill"
    ///
    /// What really is different are two things, and both for the better. The first: logging in and
    /// registering no longer block the window; they go through <see cref="Task.Run(Action)"/> and meanwhile
    /// the buttons grey out. Before, the HTTP call was made on the interface thread with a
    /// clock cursor, and a slow server left the window frozen. The second: the stored accounts
    /// go encrypted, see <see cref="Security.SecretStore"/>.
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

            /// <summary>The character shown in the row, once the server has said so.</summary>
            public LauncherService.Character? Personaje { get; set; }

            /// <summary>Its portrait, drawn with the client's bones. Null until there is one.</summary>
            public Avalonia.Media.Imaging.Bitmap? Retrato { get; set; }
        }

        private readonly List<TeamAccount> _cuentas = new();
        private readonly DispatcherTimer _reloj = new() { Interval = TimeSpan.FromSeconds(2) };

        /// <summary>The launcher's three screens.</summary>
        /// <remarks>
        /// Before there were none: logging in, the team, the language, the music, the client's path and
        /// the server's status were stacked in the same 350-pixel column, and the
        /// screen to show was decided with an «I have logged in or not» boolean. That leaves the
        /// settings —things touched once— competing for space with the play button.
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

        /// <summary>Whoever draws the portraits, taking them from the Dofus client's bones.</summary>
        /// <remarks>
        /// It is the same one Studio uses for the maps' NPCs. It keeps inside what it has already
        /// drawn, so one per window and not one per row. It is closed when the window closes.
        /// </remarks>
        private readonly Jondo.Unity.Sprites.NpcSprites _retratos = new()
        {
            // Four times the slot. The drawer smooths nothing —one sample per pixel— so
            // at the card's height the edges came out jagged; drawing big and letting
            // Avalonia shrink it, the shrinking acts as smoothing. It sets the direction on its own:
            // facing front, which is what is asked of a portrait.
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
        /// Recovers the accounts from the previous time and checks with the server whether their session is valid.
        /// </summary>
        /// <remarks>
        /// This was in the constructor and there it did harm: it is up to eight HTTP requests, one per
        /// account, and the window was not drawn until they all finished. With the server off
        /// it was eight timeouts in a row —a good half-dozen seconds— with the screen
        /// black and nothing saying the launcher was alive.
        ///
        /// Now the window comes out first and the accounts appear when the server answers. What
        /// does not change is what is checked: without asking, anyone who
        /// had a stored account was taken as logged in even if the server had rejected their credential, and what was left was
        /// a window claiming to be logged in with a play button that failed.
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

            // If the server has rejected ALL the stored sessions, one has to log in again:
            // the accounts section is shown instead of a team with a button that would fail.
            if (vivas.Count == 0)
            {
                _seccion = Seccion.Cuentas;
                Avisar(_textos.SessionExpiredError);
            }

            RefrescarResumen();
            Recolocar();

            // And the portraits at the very end, which is what takes longest and is least needed
            // to be able to play.
            await CargarLosRetratosAsync();
        }

        /// <summary>Puts in made-up accounts to be able to photograph the play screen.</summary>
        /// <remarks>
        /// Only the screenshot harness uses it. Without this, a freshly opened window without stored
        /// accounts shows the empty state, which is exactly the screen NOT to look at when
        /// working on the team's design.
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

        /// <summary>Asks the server for the characters and draws their portrait.</summary>
        /// <remarks>
        /// Two separate things and in two different places on purpose:
        ///
        ///   - <b>WHAT to draw</b> is said by the server. The look string is in the
        ///     database, and the launcher does not touch it: it is what is handed out to the players and only carries
        ///     the contract. That is one request per account, and it goes off the interface thread.
        ///
        ///   - <b>DRAWING IT</b> is done here, with the Dofus client's bones, just as
        ///     Studio does with the NPCs. We carry not a single portrait inside the executable.
        ///
        /// It goes at the very end and without blocking anything: the window is already on screen and the team can already
        /// be used. The portraits appear whenever they appear, and if the client is not where it is
        /// believed to be, they do not appear and the card reads the same.
        /// </remarks>
        private async Task CargarLosRetratosAsync()
        {
            foreach (var cuenta in _cuentas)
            {
                string token = cuenta.Token;
                var personajes = await Task.Run(() => LauncherService.CharactersOf(token));
                if (personajes.Count == 0) continue;

                // The highest level one: it is the one people recognise as «their» character.
                personajes.Sort((a, b) => b.Level.CompareTo(a.Level));
                cuenta.Personaje = personajes[0];

                try
                {
                    cuenta.Retrato = _retratos.Of(personajes[0].Look);
                }
                catch (Exception ex)
                {
                    // A look that cannot be drawn cannot leave the launcher without a team.
                    Program.LogDebug($"[Lanzador] Sin retrato para {personajes[0].Name}: {ex.Message}");
                }
            }

            RefrescarFilasDeCuentas();
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  The window's life
        // ═══════════════════════════════════════════════════════════════════════

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            AplicarIdioma();
            Recolocar();

            // The music starts on its own, just like the web interface's autoplay.
            if (OperatingSystem.IsWindows())
            {
                _musica = new MusicPlayer(Path.Combine(LauncherSkin.AssetsFolder, "theme.mp3"));
                if (_musica.Available) _musica.Play();
            }
            RefrescarBotonDeMusica();

            MirarElEstado();
            _reloj.Start();

            AlFrente();

            // And the stored accounts, with the window already on screen.
            _ = CargarLasCuentasAsync();
        }

        /// <summary>
        /// Brings the window to the front on opening.
        /// </summary>
        /// <remarks>
        /// Windows does NOT give the foreground to a window created by a process that was not the
        /// active one, so a bare <c>Activate()</c> is not enough: the window opens BEHIND whatever
        /// was on screen and the only sign that the launcher started is the music.
        /// Marking it «always on top» for an instant is what gets around that rule; it is removed
        /// right after so that from then on it behaves like any other.
        ///
        /// The Windows Forms version had this very thing and on migrating to Avalonia it was left as a
        /// loose Activate(). The result was a launcher that «does not start»: it started, opened its
        /// window, and stayed under the browser.
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
                // Not being able to come to the front is no reason to fail: the window is already there,
                // just underneath.
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _reloj.Stop();
            // A pack download in flight stops here; its journal keeps what was done.
            _packCancelar?.Cancel();
            _musica?.Dispose();
            _retratos.Dispose();
            base.OnClosed(e);

            // Closing the window NO longer shuts down the emulator: it only ends this process. The server is
            // another program and carries on with whatever players it has inside.
            Program.RequestShutdown("se ha cerrado la ventana del lanzador");
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  What is seen at each moment
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>Shows the section due and switches off the other two.</summary>
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

            // With no accounts yet, the play screen does not show an empty list and a button that
            // does nothing: it shows what has to be done and the shortcut to do it.
            bool hayCuentas = _cuentas.Count > 0;
            ListaDeCuentas.IsVisible = hayCuentas;
            EquipoVacio.IsVisible = !hayCuentas;
            BotonJugar.IsVisible = hayCuentas;
            BotonTodas.IsVisible = hayCuentas;

            // And with the team full it greys out instead of hiding, with the reason on top:
            // a button that disappears leaves one wondering whether it was ever there.
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
            // The packs can change behind the launcher's back (a client update, a folder
            // deleted by hand), so their rows are read again each time the settings open.
            if (_seccion == Seccion.Ajustes) RefrescarPacks();
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

            // The titles of the three sections and those of the settings. They go here and not in the XAML
            // because they change with the language like everything else.
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

            // The language also rules over the game: it is the --langCode it starts with. That is why
            // the path row shows it, so one does not have to guess which language it will open in.
            RefrescarRutaDelCliente();
            RefrescarResumen();
            RefrescarBotonDeMusica();
            RefrescarEstado();
            RefrescarPacks();
        }

        /// <summary>The tabs' spaced title, which on the website was the letter-spacing.</summary>
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

        /// <summary>Long paths cut in the middle: the end is what identifies the file.</summary>
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
            // «2 seleccionado(s) · 0 activo(s)» did not say what «activo» was. Now it says.
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

        /// <summary>An account's little box: portrait, name, level and the mark of whether it goes.</summary>
        /// <remarks>
        /// It was a line of text with a little square and the nickname. Now it is a card with the
        /// character drawn, because in a team of eight what one recognises at a glance is the
        /// face, not the account number.
        ///
        /// The portrait comes from the CLIENT's bones, not from any drawing we carry
        /// inside: the server says the look string and <see cref="Jondo.Unity.Sprites.NpcSprites"/>
        /// paints it. If the client is not where it is believed to be, or that string cannot be drawn, the slot
        /// keeps the name's initial and the card still reads the same.
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

            // The portraits come out at about 75 by 96, so the slot goes with that proportion and the
            // character fits WHOLE. With a squarer box aligned to the bottom one saw its
            // legs and little else.
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

        /// <summary>The initial shown while there is no portrait.</summary>
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
            // It said «MÚSICA: ON», which is its state and not what happens on pressing it. A button is
            // named after what it does.
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
        /// Greys out the fields while they cannot be used.
        /// </summary>
        /// <remarks>
        /// Two reasons and not one: that the server does not answer, and that there is a request in progress.
        /// The second was missing and that is why «entrar» could be pressed twice and send two requests.
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
        //  The pulse
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
        //  What can be pressed
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
                        // Not being able to suggest a folder does not prevent choosing a file.
                    }
                }
            }

            var elegidos = await StorageProvider.OpenFilePickerAsync(opciones);
            string? ruta = elegidos.Count > 0 ? elegidos[0].TryGetLocalPath() : null;
            if (string.IsNullOrEmpty(ruta)) return;

            LauncherPreferences.ClientExecutable = ruta;
            RefrescarRutaDelCliente();
            RefrescarPacks();
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Logging in, registering and playing
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Logs in: through the website if there is one, and if not with username and password.
        /// </summary>
        /// <remarks>
        /// The day the website exists, <see cref="LauncherPreferences.WebSite"/> stops being empty
        /// and this method starts opening the browser without anything else having to be touched. See
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
                // Off the interface thread: it is a network request and it used to freeze the
                // window while it lasted.
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

                // The website returns a voucher; the game server is the one that says which account it
                // belongs to. With that the team card can now be built.
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

            // Just logged in, what one wants is to play.
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

            // Starting the client silences the launcher's music.
            if (_musica != null && _musica.Playing)
            {
                _musica.Stop();
                RefrescarBotonDeMusica();
            }

            RefrescarResumen();
            RefrescarFilasDeCuentas();

            // No confirmation window: the client opening already confirms it, and a modal
            // dialog would stay on top of the game waiting for someone to close it.
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
