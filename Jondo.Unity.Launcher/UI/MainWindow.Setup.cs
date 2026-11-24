using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Jondo.Unity.Launcher.Setup;
using Jondo.Unity.Launcher.UI.Widgets;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// The window's installer and updater: the page where the setup is chosen, the progress of an
    /// installation or an update step by step, the update that has to be made before playing,
    /// and the installation's settings.
    /// </summary>
    public sealed partial class MainWindow
    {
        /// <summary>What the card shows besides the three sections.</summary>
        private enum Vista { Normal, Instalar, Progreso }

        private Vista _vista = Vista.Normal;

        // ─── The setup's choices ───────────────────────────────────────────────

        private string _carpetaInstalacion = InstallLayout.DefaultRoot;
        private bool _descargarCliente;
        private string _clienteElegido = "";
        private bool _servidorRemoto;
        private bool _accesos = true;
        private long _tamanoCliente;

        /// <summary>True when the size is the CDN's files; false when it is the README's archive.</summary>
        private bool _clienteDelCdn = true;

        /// <summary>Set once the size is being asked for, so switching back and forth asks once.</summary>
        private bool _midiendoCliente;

        /// <summary>The client to download: Windows, or Linux where it can be played.</summary>
        private string _plataformaCliente = ClientCdn.HostPlatform;

        // ─── What is running ───────────────────────────────────────────────────

        /// <summary>The steps of the work on screen.</summary>
        private List<SetupStep> _pasos = new();
        private readonly DispatcherTimer _relojProgreso = new() { Interval = TimeSpan.FromMilliseconds(150) };
        private Func<double> _fraccion = () => 0;
        private Func<TimeSpan?> _restante = () => null;
        private readonly System.Diagnostics.Stopwatch _cronometro = new();
        private CancellationTokenSource? _cancelarTrabajo;
        private bool _trabajando;
        private string _tituloTrabajo = "";

        /// <summary>What the progress view's button does when the work ends.</summary>
        private Action? _alTerminar;
        private Action? _alReintentar;

        /// <summary>The newer release found, which has to be installed before playing. Null while there is none.</summary>
        private UpdateCheck? _actualizacion;

        // ═══════════════════════════════════════════════════════════════════════
        //  The mode the window opens in
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>Called on opening: the installer for the setup, the update check for an installation.</summary>
        private void PrepararElModo()
        {
            _relojProgreso.Tick += (_, _) => RefrescarProgreso();
            RastroDePasos.HoveredChanged += (_, _) => { if (_pasos.Count > 0) RefrescarProgreso(); };

            if (Program.UninstallRequested)
            {
                Raiz.IsVisible = false;
                _ = DesinstalarAsync(desdeWindows: true);
                return;
            }

            if (Program.Mode == Program.LaunchMode.Setup)
            {
                PrepararElInstalador();
                MostrarVista(Vista.Instalar);
                return;
            }

            if (Program.RelayError.Length > 0)
                Avisar(SetupTexts.RelayFailed(_idioma, LauncherPreferences.ServerHost, Program.RelayError));

            if (Program.Installation is { } layout)
            {
                var state = layout.Load();
                if (!state.Complete)
                {
                    // An installation that stopped halfway: it carries on where it stopped.
                    _ = RepararAsync(automatica: true);
                    return;
                }
                if (Program.JustUpdatedTo.Length > 0) Avisar(SetupTexts.Updated(_idioma, Program.JustUpdatedTo), bueno: true);
                _ = BuscarActualizacionAsync(manual: false);
            }
        }

        private void MostrarVista(Vista vista)
        {
            _vista = vista;
            Recolocar();
        }

        /// <summary>The part of Recolocar for the installer's views; true when it took the window.</summary>
        private bool RecolocarInstalador()
        {
            bool normal = _vista == Vista.Normal;
            // The sections and the status are the installed launcher's; the setup has its language there instead.
            Secciones.IsVisible = normal;
            PildoraEstado.IsVisible = normal;
            IdiomaInstalador.IsVisible = _vista == Vista.Instalar;
            PanelInstalar.IsVisible = _vista == Vista.Instalar;
            PanelProgreso.IsVisible = _vista == Vista.Progreso;
            if (normal) return false;

            PanelJugar.IsVisible = false;
            PanelCuentas.IsVisible = false;
            PanelAjustes.IsVisible = false;
            return true;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  The installer's page
        // ═══════════════════════════════════════════════════════════════════════

        private void PrepararElInstalador()
        {
            // A client already on this machine is offered first: no 12 GB download for nothing.
            string encontrado = ClientInstallation.Find()?.Executable ?? "";
            _clienteElegido = encontrado;
            _descargarCliente = encontrado.Length == 0;
            _servidorRemoto = !LauncherPreferences.ServerIsLocal;
            CampoServidorInstalar.Text = _servidorRemoto ? LauncherPreferences.ServerHost : "";
            CampoRepositorio.Text = ReleaseFeed.DefaultRepository;
            RefrescarInstalador();
            if (_descargarCliente) _ = MedirElClienteAsync();
        }

        /// <summary>
        /// The size of the client to download, for the option and the room it needs: the CDN's
        /// files for the version the README names (its manifest is kept for the texture packs
        /// too), or the README's archive when the CDN does not serve that version. Only asked for
        /// when the download is chosen: the manifest is 50 MB.
        /// </summary>
        private async Task MedirElClienteAsync()
        {
            if (_midiendoCliente) return;
            _midiendoCliente = true;
            // A switch of platform while this one is asked for makes its answer stale.
            string plataforma = _plataformaCliente;
            try
            {
                string repositorio = ReleaseFeed.Normalize(CampoRepositorio.Text) ?? ReleaseFeed.DefaultRepository;
                (string Version, string Url)? enlace = null;
                try { enlace = ReleaseFeed.ClientLinkIn(await new ReleaseFeed(repositorio).ReadmeAsync("HEAD")); }
                catch (Exception ex) { Console.WriteLine($"[Setup] The README could not be read: {ex.Message}"); }
                string version = enlace?.Version ?? ClientDownload.Version;

                if (await ClientCdn.IsServedAsync(Http.Shared, version, plataforma, CancellationToken.None))
                {
                    long tamano = await Task.Run(async () =>
                        ClientCdn.Size(await ClientCdn.ManifestAsync(Http.Shared, version, plataforma, null, CancellationToken.None)));
                    if (plataforma != _plataformaCliente) return;
                    _tamanoCliente = tamano;
                    _clienteDelCdn = true;
                }
                else if (plataforma == ClientCdn.Windows && enlace is { } e)
                {
                    var (_, size) = await ClientDownload.ProbeAsync(e.Url, CancellationToken.None);
                    _tamanoCliente = size;
                    _clienteDelCdn = false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Setup] The client's size could not be read: {ex.Message}");
                _midiendoCliente = false;
            }
            RefrescarInstalador();
        }

        /// <summary>The room the chosen client needs on the drive, or 0 when it is not being downloaded or its size is not known yet.</summary>
        private long EspacioParaElCliente()
            => !_descargarCliente || _tamanoCliente <= 0 ? 0
               : _clienteDelCdn ? ClientCdn.SpaceNeeded(_tamanoCliente) : ClientDownload.SpaceNeeded(_tamanoCliente);

        private void AplicarTextosInstalador()
        {
            InstalarEs.Classes.Set("on", _idioma == Language.Es);
            InstalarEn.Classes.Set("on", _idioma == Language.En);
            InstalarFr.Classes.Set("on", _idioma == Language.Fr);
            InstalarPt.Classes.Set("on", _idioma == Language.Pt);

            TituloInstalar.Text = SetupTexts.InstallTitle(_idioma);
            IntroInstalar.Text = SetupTexts.InstallIntro(_idioma);
            RotuloCarpeta.Text = SetupTexts.Folder(_idioma);
            DescCarpeta.Text = SetupTexts.FolderDesc(_idioma);
            RotuloClienteInstalar.Text = SetupTexts.ClientChoice(_idioma);
            DescClienteInstalar.Text = SetupTexts.ClientDesc(_idioma);
            TextoTengoCliente.Text = SetupTexts.ClientExisting(_idioma);
            DescTengoCliente.Text = SetupTexts.ClientExistingDesc(_idioma);
            DescDescargarCliente.Text = SetupTexts.ClientDownloadDesc(_idioma);
            TextoInstalarClienteWindows.Text = SetupTexts.PlatformChoice(ClientCdn.Windows);
            TextoInstalarClienteLinux.Text = SetupTexts.PlatformChoice(ClientCdn.Linux);
            RotuloServidorInstalar.Text = SetupTexts.Connection(_idioma);
            DescServidorInstalar.Text = Textos.DescServidor(_idioma);
            TextoServidorLocal.Text = SetupTexts.ConnectionLocal(_idioma);
            DescServidorLocal.Text = SetupTexts.ConnectionLocalDesc(_idioma);
            TextoServidorRemoto.Text = SetupTexts.ConnectionRemoteShort(_idioma);
            DescServidorRemoto.Text = SetupTexts.ConnectionRemoteDesc(_idioma);
            CampoServidorInstalar.Watermark = SetupTexts.ConnectionAddress(_idioma);
            RotuloRepositorio.Text = SetupTexts.Repository(_idioma);
            PieRepositorio.Text = SetupTexts.RepositoryNote(_idioma);
            CampoRepositorio.Watermark = SetupTexts.RepositoryShape(_idioma);
            TextoAccesos.Text = SetupTexts.Shortcuts(_idioma);
            TextoInstalar.Text = SetupTexts.InstallButton(_idioma);
            TextoVolver.Text = SetupTexts.Back(_idioma);

            // The settings that belong to the installation.
            RotuloServidorAjustes.Text = SetupTexts.Connection(_idioma);
            TextoAjusteLocal.Text = SetupTexts.ConnectionLocal(_idioma);
            TextoAjusteRemoto.Text = SetupTexts.ConnectionRemoteShort(_idioma);
            CampoServidorAjustes.Watermark = SetupTexts.ConnectionAddress(_idioma);
            GuardarServidor.Content = SetupTexts.Save(_idioma);
            RotuloInstalacion.Text = SetupTexts.InstallationSection(_idioma);
            RotuloRepositorioAjustes.Text = SetupTexts.Repository(_idioma);
            GuardarRepositorio.Content = SetupTexts.Save(_idioma);
            TextoBuscarActualizaciones.Text = SetupTexts.CheckUpdates(_idioma);
            TextoRepararInstalacion.Text = SetupTexts.RepairShort(_idioma);
            ToolTip.SetTip(RepararInstalacion, SetupTexts.Repair(_idioma));
            TextoDesinstalar.Text = SetupTexts.Uninstall(_idioma);

            RefrescarInstalador();
            RefrescarAjustesDeInstalacion();
            RefrescarBotonDeJugar();
        }

        private void RefrescarInstalador()
        {
            TextoCarpeta.Text = _carpetaInstalacion;
            bool pideAdministrador = SystemIntegration.NeedsElevation(_carpetaInstalacion);
            PieCarpeta.Text = pideAdministrador ? SetupTexts.FolderNote(_idioma) : "";
            AvisoCarpeta.IsVisible = pideAdministrador;

            OpcionTengoCliente.Classes.Set("on", !_descargarCliente);
            OpcionDescargarCliente.Classes.Set("on", _descargarCliente);
            TextoDescargarCliente.Text = SetupTexts.ClientDownload(_idioma, _tamanoCliente > 0 ? Format.Bytes(_tamanoCliente) : "≈ 12 GB");
            DescDescargarCliente.Text = _clienteDelCdn ? SetupTexts.ClientDownloadDesc(_idioma) : SetupTexts.ClientDownloadReadmeDesc(_idioma);
            BotonClienteInstalar.IsVisible = !_descargarCliente;
            TextoClienteInstalar.Text = _clienteElegido.Length > 0 ? _clienteElegido : SetupTexts.ClientPick(_idioma);

            // The platform, for a download: on Windows only its own client can be played, and
            // the Linux one stays in sight, greyed, saying where it can be had.
            PlataformaInstalar.IsVisible = _descargarCliente;
            InstalarClienteWindows.Classes.Set("on", _plataformaCliente == ClientCdn.Windows);
            InstalarClienteLinux.Classes.Set("on", _plataformaCliente == ClientCdn.Linux);
            bool linuxAqui = ClientCdn.RunsHere(ClientCdn.Linux);
            InstalarClienteLinux.IsEnabled = linuxAqui;
            ToolTip.SetTip(InstalarClienteLinux, linuxAqui ? null : SetupTexts.LinuxOnlyOnLinux(_idioma));
            string notaPlataforma = linuxAqui ? SetupTexts.PlatformNote(_idioma, _plataformaCliente) : SetupTexts.LinuxOnlyOnLinux(_idioma);
            PiePlataformaInstalar.Text = notaPlataforma;
            PiePlataformaInstalar.IsVisible = notaPlataforma.Length > 0;

            // The drive the folder is on, and whether the client fits there.
            var disco = Disco(_carpetaInstalacion);
            long falta = EspacioParaElCliente();
            bool noCabe = disco is { } d0 && falta > d0.Libre;
            CajaDisco.IsVisible = disco != null;
            if (disco is { } d)
            {
                TextoDisco.Text = SetupTexts.Drive(_idioma, d.Raiz, Format.Bytes(d.Total));
                LibreDisco.Text = SetupTexts.DriveFree(_idioma, Format.Bytes(d.Libre));
                LibreDisco.Foreground = new SolidColorBrush(noCabe ? LauncherSkin.Danger : LauncherSkin.CardText);
                BarraDisco.Fraction = d.Total > 0 ? 1 - (double)d.Libre / d.Total : 0;
                BarraDisco.Failed = noCabe;
            }

            string pie = "";
            if (falta > 0)
            {
                pie = noCabe ? SetupTexts.NotEnoughSpace(_idioma, Format.Bytes(falta), Format.Bytes(disco!.Value.Libre))
                             : SetupTexts.SpaceNeeded(_idioma, Format.Bytes(falta));
            }
            else if (!_descargarCliente && OperatingSystem.IsWindows())
            {
                pie = SetupTexts.GenerationWarning(_idioma);
            }
            PieClienteInstalar.Text = pie;
            PieClienteInstalar.IsVisible = pie.Length > 0;
            PieClienteInstalar.Foreground = new SolidColorBrush(noCabe ? LauncherSkin.Danger : LauncherSkin.TextTertiary);

            OpcionServidorLocal.Classes.Set("on", !_servidorRemoto);
            OpcionServidorRemoto.Classes.Set("on", _servidorRemoto);
            CampoServidorInstalar.IsVisible = _servidorRemoto;
            OpcionAccesos.Classes.Set("on", _accesos);
        }

        /// <summary>The drive a folder would go on: its name, size and free space; null when it cannot be read.</summary>
        private static (string Raiz, long Total, long Libre)? Disco(string folder)
        {
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(folder));
                if (string.IsNullOrEmpty(root)) return null;
                var drive = new DriveInfo(root);
                if (!drive.IsReady) return null;
                string nombre = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return (nombre.Length > 0 ? nombre : root, drive.TotalSize, drive.AvailableFreeSpace);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return null;
            }
        }

        private static long SafeFreeSpace(string folder)
        {
            try { return ClientDownload.FreeSpace(folder); }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return long.MaxValue; }
        }

        private async void AlPulsarCarpeta(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var elegidas = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = SetupTexts.Folder(_idioma),
                AllowMultiple = false,
            });
            string? ruta = elegidas.Count > 0 ? elegidas[0].TryGetLocalPath() : null;
            if (string.IsNullOrEmpty(ruta)) return;
            // A folder with things in it gets one of its own inside; an empty one, or ours, is used as it is.
            bool vacia = !Directory.Exists(ruta) || !Directory.EnumerateFileSystemEntries(ruta).Any();
            bool nuestra = File.Exists(Path.Combine(ruta, InstallLayout.StateFileName))
                           || Path.GetFileName(ruta.TrimEnd(Path.DirectorySeparatorChar)).Equals(InstallLayout.FolderName, StringComparison.OrdinalIgnoreCase);
            _carpetaInstalacion = vacia || nuestra ? ruta : Path.Combine(ruta, InstallLayout.FolderName);
            RefrescarInstalador();
        }

        private void AlElegirCliente(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _descargarCliente = (remitente as Button)?.Tag as string == "descargar";
            RefrescarInstalador();
            if (_descargarCliente) _ = MedirElClienteAsync();
        }

        private void AlElegirPlataformaInstalar(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
        {
            string plataforma = (remitente as Button)?.Tag as string ?? ClientCdn.HostPlatform;
            if (plataforma == _plataformaCliente || !ClientCdn.RunsHere(plataforma)) return;
            _plataformaCliente = plataforma;
            // Each platform has its own manifest, and its size is asked for again.
            _tamanoCliente = 0;
            _clienteDelCdn = true;
            _midiendoCliente = false;
            RefrescarInstalador();
            _ = MedirElClienteAsync();
        }

        /// <summary>The file types a client can be chosen by: Dofus.exe, and on Linux its own client too.</summary>
        private static FilePickerFileType[] TiposDeCliente()
        {
            var nombres = OperatingSystem.IsWindows()
                ? new[] { ClientCdn.ExecutableFor(ClientCdn.Windows) }
                : new[] { ClientCdn.ExecutableFor(ClientCdn.Linux), ClientCdn.StartScript, ClientCdn.ExecutableFor(ClientCdn.Windows) };
            return new[]
            {
                new FilePickerFileType(string.Join(", ", nombres)) { Patterns = nombres },
                FilePickerFileTypes.All,
            };
        }

        private async void AlPulsarClienteInstalar(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var elegidos = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Textos.WhereIsTheClient(_idioma),
                AllowMultiple = false,
                FileTypeFilter = TiposDeCliente(),
            });
            string? ruta = elegidos.Count > 0 ? elegidos[0].TryGetLocalPath() : null;
            if (string.IsNullOrEmpty(ruta)) return;
            ruta = ClientCdn.ExecutableChosen(ruta);
            if (!ClientCdn.RunsHere(ClientCdn.PlatformOf(ruta))) { Avisar(SetupTexts.ClientCannotRunHere(_idioma)); return; }
            _clienteElegido = ruta;
            RefrescarInstalador();
        }

        private void AlElegirServidor(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _servidorRemoto = (remitente as Button)?.Tag as string == "remoto";
            RefrescarInstalador();
        }

        private void AlPulsarAccesos(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _accesos = !_accesos;
            RefrescarInstalador();
        }

        private void AlPulsarInstalar(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
        {
            QuitarElAviso();
            string? repositorio = ReleaseFeed.Normalize(CampoRepositorio.Text);
            if (repositorio == null) { Avisar(SetupTexts.BadRepository(_idioma)); return; }
            string servidor = (CampoServidorInstalar.Text ?? "").Trim();
            if (_servidorRemoto && servidor.Length == 0) { Avisar(SetupTexts.BadAddress(_idioma)); return; }
            if (!_descargarCliente && !File.Exists(_clienteElegido)) { Avisar(SetupTexts.ClientNotFound(_idioma)); return; }
            if (EspacioParaElCliente() is > 0 and var falta)
            {
                long libre = SafeFreeSpace(_carpetaInstalacion);
                if (libre < falta) { Avisar(SetupTexts.NotEnoughSpace(_idioma, Format.Bytes(falta), Format.Bytes(libre))); return; }
            }

            var opciones = new InstallOptions
            {
                Root = _carpetaInstalacion,
                Repository = repositorio,
                ExistingClient = _descargarCliente ? "" : Path.GetFullPath(_clienteElegido),
                ClientPlatform = _plataformaCliente,
                ServerHost = _servidorRemoto ? servidor : "",
                Shortcuts = _accesos,
            };
            _ = InstalarAsync(opciones, alTerminar: () =>
            {
                // The setup's job ends here: the installed launcher opens, and this one closes.
                Program.StartAgain(new InstallLayout(opciones.Root).LauncherExecutable);
                Close();
            });
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Installing, repairing
        // ═══════════════════════════════════════════════════════════════════════

        private async Task InstalarAsync(InstallOptions opciones, Action alTerminar, bool reparando = false)
        {
            if (_trabajando) return;
            var instalador = new Installer(opciones, _idioma);
            EmpezarTrabajo(reparando ? SetupTexts.Repairing(_idioma) : SetupTexts.Installing(_idioma),
                           instalador.Steps, () => instalador.Fraction, () => instalador.Remaining);
            _alReintentar = () => _ = InstalarAsync(opciones, alTerminar, reparando);
            try
            {
                await Task.Run(() => instalador.RunAsync(_cancelarTrabajo!.Token));
                if (reparando)
                    TerminarTrabajo(SetupTexts.Repaired(_idioma), SetupTexts.RepairedNote(_idioma), SetupTexts.BackToLauncher(_idioma), alTerminar);
                else
                {
                    // Nothing left to ask: the summary shows for a moment and the launcher, with its
                    // sign-in and the server already up, takes over. The button is there for the
                    // impatient, and the timer does not press it twice.
                    TerminarTrabajo(SetupTexts.Done(_idioma), SetupTexts.DoneOpening(_idioma), SetupTexts.OpenLauncher(_idioma), alTerminar);
                    var abrir = _alTerminar;
                    DispatcherTimer.RunOnce(() => { if (_alTerminar == abrir) { _alTerminar = null; abrir?.Invoke(); } },
                                            TimeSpan.FromSeconds(3));
                }
            }
            catch (Exception ex) when (ex is SetupException or OperationCanceledException or IOException or UnauthorizedAccessException)
            {
                FallarTrabajo(ex);
            }
        }

        /// <summary>
        /// Runs the installer again over this installation: whatever is missing or broken is put
        /// back, MelonLoader and the mods included, and the latest release with them.
        /// </summary>
        private async Task RepararAsync(bool automatica)
        {
            if (Program.Installation is not { } layout || _trabajando) return;
            var state = layout.Load();
            var opciones = new InstallOptions
            {
                Root = layout.Root,
                Repository = state.Repository.Length > 0 ? state.Repository : ReleaseFeed.DefaultRepository,
                ExistingClient = File.Exists(state.Client) ? state.Client : "",
                // A client gone from disk comes back for the platform it was.
                ClientPlatform = state.Client.Length > 0 && ClientCdn.RunsHere(ClientCdn.PlatformOf(state.Client))
                    ? ClientCdn.PlatformOf(state.Client) : ClientCdn.HostPlatform,
                ServerHost = LauncherPreferences.ServerIsLocal ? "" : LauncherPreferences.ServerHost,
                Shortcuts = false,
            };
            Console.WriteLine($"[Setup] Repairing {layout.Root} ({(automatica ? "it had stopped halfway" : "asked for")}).");
            // The server runs from these files: it is closed first, and started again with the launcher.
            await Task.Run(() => Updater.StopServerAsync(layout, CancellationToken.None));
            // One asked for is a repair; one that had stopped halfway is still the installation.
            await InstalarAsync(opciones, alTerminar: () =>
            {
                Program.StartAgain(layout.LauncherExecutable);
                Close();
            }, reparando: !automatica);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Updating
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Asks the repository for a newer release. With one, playing waits: the play button
        /// becomes the update button. Without a connection the game is not held hostage; the
        /// check is only reported when the player asked for it.
        /// </summary>
        private async Task BuscarActualizacionAsync(bool manual)
        {
            if (Program.Installation is not { } layout) return;
            var state = layout.Load();
            BuscarActualizaciones.IsEnabled = false;
            try
            {
                _actualizacion = await Task.Run(() => Updater.CheckAsync(layout, state, CancellationToken.None));
                if (_actualizacion is { } nueva)
                {
                    Console.WriteLine($"[Update] {state.Version} -> {nueva.Release.Tag} available: " +
                                      $"{nueva.Plan.Fetch.Count} file(s), {Format.Bytes(nueva.DownloadSize)}" +
                                      (nueva.Plan.Full ? " (full zip)." : "."));
                    Avisar(SetupTexts.UpdateRequired(_idioma, state.Version, nueva.Release.Tag) + Environment.NewLine +
                           SetupTexts.UpdateSize(_idioma, nueva.Plan.Fetch.Count, Format.Bytes(nueva.DownloadSize), nueva.Plan.Full),
                           Tono.Atencion);
                    if (manual) _seccion = Seccion.Jugar;
                }
                else if (manual)
                {
                    PieInstalacion.Text = SetupTexts.UpToDate(_idioma, state.Version);
                }
            }
            catch (Exception ex) when (ex is SetupException or System.Net.Http.HttpRequestException or TaskCanceledException
                                           or System.Text.Json.JsonException or IOException)
            {
                Console.WriteLine($"[Update] The check failed: {ex.Message}");
                if (manual) PieInstalacion.Text = SetupTexts.UpdateCheckFailed(_idioma, ex.Message);
            }
            finally
            {
                BuscarActualizaciones.IsEnabled = true;
                RefrescarBotonDeJugar();
                Recolocar();
            }
        }

        /// <summary>The play button, or the update button while an update is due.</summary>
        private void RefrescarBotonDeJugar()
        {
            bool actualizar = _actualizacion != null;
            BotonJugar.Classes.Set("green", !actualizar);
            BotonJugar.Classes.Set("purple", actualizar);
            RefrescarResumen();
        }

        private async Task ActualizarAsync()
        {
            if (Program.Installation is not { } layout || _actualizacion is not { } check || _trabajando) return;
            if (LauncherPreferences.ServerIsLocal && Updater.ServersOf(layout.ServerExecutable).Count > 0 && _servidorEnLinea)
            {
                bool seguir = await Dialogs.ConfirmAsync(this, Title ?? "Jondo", SetupTexts.ServerRunning(_idioma),
                                                         SetupTexts.UpdateButton(_idioma, check.Release.Tag), SetupTexts.Cancel(_idioma),
                                                         peligro: false);
                if (!seguir) return;
            }

            var actualizador = new Updater(layout, check, _idioma);
            EmpezarTrabajo(SetupTexts.Updating(_idioma) + " · " + check.Release.Tag, actualizador.Steps,
                           () => actualizador.Fraction, () => actualizador.Remaining);
            _alReintentar = () => _ = ActualizarAsync();
            try
            {
                await Task.Run(() => actualizador.RunAsync(_cancelarTrabajo!.Token));
                // Started again from the new files: the launcher (perhaps new itself) and, being local, the server.
                Program.StartAgain(layout.LauncherExecutable, "--updated", check.Release.Tag);
                Close();
            }
            catch (Exception ex) when (ex is SetupException or OperationCanceledException or IOException or UnauthorizedAccessException)
            {
                FallarTrabajo(ex, SetupTexts.UpdateFailed(_idioma));
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  The progress view
        // ═══════════════════════════════════════════════════════════════════════
        //
        // The steps go across the card as a trail of nodes -- done, running, ahead, each with a
        // shape of its own -- and the card under it shows one step whole: the one running, or the
        // one pointed at. At the end the card gives way to every step and what it took.

        private void EmpezarTrabajo(string titulo, List<SetupStep> pasos, Func<double> fraccion, Func<TimeSpan?> restante)
        {
            QuitarElAviso();
            _trabajando = true;
            _cancelarTrabajo?.Dispose();
            _cancelarTrabajo = new CancellationTokenSource();
            _tituloTrabajo = titulo;
            _fraccion = fraccion;
            _restante = restante;
            _alTerminar = null;
            _pasos = pasos;
            _cronometro.Restart();

            EyebrowProgreso.Text = "JONDO EMULATOR";
            TituloProgreso.Text = titulo;
            NotaProgreso.Text = "";
            NotaProgreso.IsVisible = false;
            BotonesProgreso.IsVisible = false;
            BotonProgreso.IsVisible = false;
            BotonVolver.IsVisible = false;
            BarraTotal.Failed = false;
            BarraTotal.Fraction = 0;
            BarraTotal.Working = true;

            RastroDePasos.SetSteps(pasos);
            TarjetaPaso.IsVisible = true;
            CajaResumen.IsVisible = false;
            MostrarVista(Vista.Progreso);
            RefrescarProgreso();
            _relojProgreso.Start();
        }

        private void TerminarTrabajo(string titulo, string nota, string boton, Action alTerminar)
        {
            _trabajando = false;
            _cronometro.Stop();
            RefrescarProgreso();
            _relojProgreso.Stop();
            BarraTotal.Working = false;
            BarraTotal.Fraction = 1;
            TituloProgreso.Text = titulo;
            PorcentajeTotal.Text = "100 %";
            TiempoTotal.Text = SetupTexts.Elapsed(_idioma, Format.Duration(_cronometro.Elapsed));

            TarjetaPaso.IsVisible = false;
            FilaSiguiente.IsVisible = false;
            LlenarResumen();
            CajaResumen.IsVisible = true;

            NotaProgreso.Text = nota;
            NotaProgreso.Foreground = new SolidColorBrush(LauncherSkin.TextSecondary);
            NotaProgreso.IsVisible = true;
            _alTerminar = alTerminar;
            TextoBotonProgreso.Text = boton;
            BotonProgreso.Classes.Set("purple", false);
            BotonProgreso.Classes.Set("green", true);
            BotonProgreso.IsVisible = true;
            BotonesProgreso.IsVisible = true;
        }

        private void FallarTrabajo(Exception ex, string? titulo = null)
        {
            _trabajando = false;
            _cronometro.Stop();
            RefrescarProgreso();
            _relojProgreso.Stop();
            BarraTotal.Working = false;
            BarraTotal.Failed = true;
            Console.WriteLine($"[Setup] Stopped: {ex}");
            TituloProgreso.Text = titulo ?? SetupTexts.Failed(_idioma);

            // The step that stopped says why in its card; without one, the note says it.
            bool conPaso = _pasos.Any(p => p.State == StepState.Failed && p.Error.Length > 1);
            NotaProgreso.Text = conPaso ? SetupTexts.FailedNote(_idioma)
                                        : ex.Message + Environment.NewLine + SetupTexts.FailedNote(_idioma);
            NotaProgreso.Foreground = new SolidColorBrush(conPaso ? LauncherSkin.TextSecondary : Color.FromRgb(0xF2, 0xA4, 0x96));
            NotaProgreso.IsVisible = true;
            FilaSiguiente.IsVisible = false;
            _alTerminar = _alReintentar;
            TextoBotonProgreso.Text = SetupTexts.Retry(_idioma);
            BotonProgreso.Classes.Set("green", false);
            BotonProgreso.Classes.Set("purple", true);
            BotonProgreso.IsVisible = true;
            BotonVolver.IsVisible = true;
            BotonesProgreso.IsVisible = true;
        }

        private void AlPulsarBotonProgreso(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
        {
            // Once: the same action again would start a second launcher.
            var accion = _alTerminar;
            if (accion != _alReintentar) _alTerminar = null;
            accion?.Invoke();
        }

        private void AlPulsarVolver(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_trabajando) return;
            MostrarVista(Program.Mode == Program.LaunchMode.Setup ? Vista.Instalar : Vista.Normal);
        }

        private static string Porcentaje(double fraccion) => $"{Math.Floor(fraccion * 1000) / 10:0.0} %";

        private void RefrescarProgreso()
        {
            double total = _fraccion();
            BarraTotal.Fraction = total;
            PorcentajeTotal.Text = Porcentaje(total);
            var restante = _restante();
            TiempoTotal.Text = _trabajando
                ? (restante is { } r ? SetupTexts.Remaining(_idioma, Format.Duration(r)) : "…")
                : SetupTexts.Elapsed(_idioma, Format.Duration(_cronometro.Elapsed));

            int hechos = _pasos.Count(p => p.State is StepState.Done or StepState.Skipped);
            PasoActual.Text = SetupTexts.StepsDone(_idioma, hechos, _pasos.Count);

            // The card shows the step pointed at; else the one running, the one that stopped, or
            // the last one reached.
            int corriendo = _pasos.FindIndex(p => p.State == StepState.Running);
            int foco = RastroDePasos.Hovered;
            if (foco < 0) foco = corriendo;
            if (foco < 0) foco = _pasos.FindIndex(p => p.State == StepState.Failed);
            if (foco < 0) foco = _pasos.FindLastIndex(p => p.State != StepState.Pending);
            if (foco < 0) foco = 0;
            RastroDePasos.Focused = foco;
            RastroDePasos.Refresh();
            PintarTarjeta(foco);

            // And what comes after the running one.
            string siguiente = "";
            if (_trabajando && corriendo >= 0)
            {
                var despues = _pasos.Skip(corriendo + 1).FirstOrDefault(p => p.State == StepState.Pending);
                siguiente = despues != null ? SetupTexts.NextStep(_idioma, despues.Title) : SetupTexts.LastStep(_idioma);
            }
            SiguientePaso.Text = siguiente;
            FilaSiguiente.IsVisible = siguiente.Length > 0;
        }

        /// <summary>One step whole: its number, its state as a chip, its title, what it is doing, its bar and its time.</summary>
        private void PintarTarjeta(int i)
        {
            if (i < 0 || i >= _pasos.Count) return;
            var paso = _pasos[i];

            NumeroPaso.Text = SetupTexts.StepOf(_idioma, i + 1, _pasos.Count);
            (string chip, Color color) = paso.State switch
            {
                StepState.Running => (SetupTexts.StateRunning(_idioma), LauncherSkin.LightGold),
                StepState.Done => (SetupTexts.StateDone(_idioma), LauncherSkin.OnlineGreen),
                StepState.Skipped => (SetupTexts.StateSkipped(_idioma), LauncherSkin.TextSecondary),
                StepState.Failed => (SetupTexts.StateFailed(_idioma), LauncherSkin.Danger),
                _ => (SetupTexts.StatePending(_idioma), LauncherSkin.TextTertiary),
            };
            TextoChipPaso.Text = chip;
            TextoChipPaso.Foreground = new SolidColorBrush(color);
            ChipPaso.Background = new SolidColorBrush(color, 0.15);
            TarjetaPaso.BorderBrush = new SolidColorBrush(paso.State switch
            {
                StepState.Running => Color.FromArgb(0x80, 0xD4, 0xAF, 0x37),
                StepState.Failed => Color.FromArgb(0x99, 0xE5, 0x60, 0x4D),
                _ => LauncherSkin.Hairline,
            });

            TituloPaso.Text = paso.Title;
            TituloPaso.Foreground = new SolidColorBrush(paso.State == StepState.Pending ? LauncherSkin.TextSecondary : LauncherSkin.CardText);
            PorcentajePaso.Text = paso.State switch
            {
                StepState.Running => Porcentaje(paso.Fraction),
                StepState.Failed => $"{Math.Floor(paso.Fraction * 100):0} %",
                StepState.Done => "100 %",
                _ => "",
            };
            PorcentajePaso.Foreground = new SolidColorBrush(paso.State switch
            {
                StepState.Failed => LauncherSkin.Danger,
                StepState.Done => LauncherSkin.OnlineGreen,
                _ => LauncherSkin.Gold,
            });

            DetallePaso.Text = paso.State switch
            {
                StepState.Failed => paso.Error,
                StepState.Pending => SetupTexts.PendingDetail(_idioma),
                StepState.Skipped => paso.Detail.Length > 0 ? paso.Detail : SetupTexts.SkippedDetail(_idioma),
                _ => paso.Detail,
            };
            DetallePaso.IsVisible = DetallePaso.Text.Length > 0;
            DetallePaso.Foreground = new SolidColorBrush(paso.State == StepState.Failed ? Color.FromRgb(0xF2, 0xA4, 0x96) : LauncherSkin.TextSecondary);

            TiempoPaso.Text = paso.State switch
            {
                StepState.Running => paso.Remaining is { } left && left.TotalSeconds >= 1 ? SetupTexts.Remaining(_idioma, Format.Duration(left)) : "…",
                StepState.Done => SetupTexts.Elapsed(_idioma, Format.Duration(paso.Elapsed)),
                _ => "",
            };

            BarraPaso.Fraction = paso.State == StepState.Done ? 1 : paso.Fraction;
            BarraPaso.Working = paso.State == StepState.Running;
            BarraPaso.Failed = paso.State == StepState.Failed;
        }

        /// <summary>The end: every step in two columns, with what it took.</summary>
        private void LlenarResumen()
        {
            ResumenPasos.Children.Clear();
            int filas = (_pasos.Count + 1) / 2;
            ResumenPasos.ColumnDefinitions = new ColumnDefinitions("*,28,*");
            ResumenPasos.RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", Math.Max(1, filas))));

            for (int i = 0; i < _pasos.Count; i++)
            {
                var paso = _pasos[i];
                bool hecho = paso.State == StepState.Done;
                var linea = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                    Margin = new Thickness(0, 5),
                };
                var icono = new Icon
                {
                    Kind = hecho ? "check" : paso.State == StepState.Failed ? "close" : "check",
                    Width = 14,
                    Height = 14,
                    Stroke = 2.6,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(hecho ? LauncherSkin.OnlineGreen : LauncherSkin.TextTertiary),
                };
                var nombre = new TextBlock
                {
                    Text = paso.Title,
                    FontSize = 12.5,
                    Margin = new Thickness(10, 0, 8, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(hecho ? LauncherSkin.CardText : LauncherSkin.TextTertiary),
                };
                var tiempo = new TextBlock
                {
                    Text = hecho ? Format.Duration(paso.Elapsed) : SetupTexts.StateSkipped(_idioma).ToLowerInvariant(),
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(LauncherSkin.TextTertiary),
                };
                Grid.SetColumn(nombre, 1);
                Grid.SetColumn(tiempo, 2);
                linea.Children.Add(icono);
                linea.Children.Add(nombre);
                linea.Children.Add(tiempo);

                Grid.SetRow(linea, i % Math.Max(1, filas));
                Grid.SetColumn(linea, i < filas ? 0 : 2);
                ResumenPasos.Children.Add(linea);
            }
        }

        /// <summary>For the screenshot harness: the setup's page, without asking the network for the client's size.</summary>
        internal void MostrarInstaladorDeMentira(bool descargar = false, string? plataforma = null)
        {
            if (plataforma != null) _plataformaCliente = plataforma;
            _clienteElegido = descargar ? "" : (ClientInstallation.Find()?.Executable ?? "");
            _descargarCliente = descargar || _clienteElegido.Length == 0;
            _tamanoCliente = 12_860_000_000;
            _clienteDelCdn = true;
            _midiendoCliente = true;
            CampoRepositorio.Text = ReleaseFeed.DefaultRepository;
            RefrescarInstalador();
            MostrarVista(Vista.Instalar);
        }

        /// <summary>For the screenshot harness: the installed launcher's sections, whatever mode the test runs in.</summary>
        internal void MostrarSeccionDeMentira(string seccion, bool instalado = false)
        {
            _vista = Vista.Normal;
            _seccion = seccion switch { "noticias" => Seccion.Noticias, "cuentas" => Seccion.Cuentas, "ajustes" => Seccion.Ajustes, _ => Seccion.Jugar };
            if (_seccion == Seccion.Ajustes) RefrescarPacks();
            Recolocar();
            if (!instalado) return;
            // As an installed launcher shows it, without one on disk.
            AjustesInstalacion.IsVisible = true;
            TextoVersionInstalada.Text = SetupTexts.InstalledVersion(_idioma, "v1.0.0", @"C:\Program Files\JondoEmu");
            CampoRepositorioAjustes.Text = ReleaseFeed.DefaultRepository;
        }

        /// <summary>For the screenshot harness: a notice of each kind (0 error, 1 good, 2 something to do).</summary>
        internal void AvisarDeMentira(string mensaje, int tono) => Avisar(mensaje, (Tono)tono);

        /// <summary>For the screenshot harness: the window in another language, without storing it.</summary>
        internal void HablarDeMentira(Language idioma)
        {
            _idioma = idioma;
            _textos = LauncherTexts.Get(idioma);
            AplicarIdioma();
            Recolocar();
        }

        /// <summary>For the screenshot harness: the progress view as it looks halfway, with made-up steps.</summary>
        internal void MostrarProgresoDeMentira(int corriendo, double fraccion, bool fallo = false, bool terminado = false)
        {
            var pasos = new List<SetupStep>
            {
                new("release", SetupTexts.StepRelease(_idioma), 1, TimeSpan.FromSeconds(2)),
                new("system", SetupTexts.StepSystem(_idioma), 1, TimeSpan.FromSeconds(2)),
                new("emulator", SetupTexts.StepEmulator(_idioma), 10, TimeSpan.FromSeconds(30)),
                new("files", SetupTexts.StepFiles(_idioma), 3, TimeSpan.FromSeconds(10)),
                new("client", SetupTexts.StepClient(_idioma), 1, TimeSpan.FromSeconds(2)),
                new("client-download", SetupTexts.StepClientDownload(_idioma), 1, TimeSpan.FromSeconds(1)),
                new("melon", SetupTexts.StepMelonLoader(_idioma), 4, TimeSpan.FromSeconds(15)),
                new("mods", SetupTexts.StepMods(_idioma), 1, TimeSpan.FromSeconds(3)),
                new("generation", SetupTexts.StepGeneration(_idioma), 30, TimeSpan.FromMinutes(8)),
                new("database", SetupTexts.StepDatabase(_idioma), 2, TimeSpan.FromSeconds(10)),
                new("finish", SetupTexts.StepFinish(_idioma), 1, TimeSpan.FromSeconds(1)),
            };
            for (int i = 0; i < pasos.Count; i++)
            {
                if (terminado || i < corriendo)
                {
                    pasos[i].Start();
                    if (i == 5) pasos[i].Skip(SetupTexts.AlreadyThere(_idioma));
                    else pasos[i].Finish();
                }
                else if (i == corriendo)
                {
                    pasos[i].Start(SetupTexts.GenerationStage(_idioma, "cpp2il"));
                    pasos[i].Report(fraccion);
                    if (fallo) pasos[i].Fail("Cpp2IL exited with code 1.");
                }
            }
            double hecho = terminado ? 1 : (corriendo + fraccion) / pasos.Count;
            EmpezarTrabajo(SetupTexts.Installing(_idioma), pasos, () => hecho, () => TimeSpan.FromMinutes(6));
            if (terminado) TerminarTrabajo(SetupTexts.Done(_idioma), SetupTexts.DoneNote(_idioma), SetupTexts.OpenLauncher(_idioma), () => { });
            else if (fallo) FallarTrabajo(new SetupException("Cpp2IL exited with code 1."));
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  The installation's settings
        // ═══════════════════════════════════════════════════════════════════════

        private bool _ajusteRemoto;

        private void RefrescarAjustesDeInstalacion()
        {
            _ajusteRemoto = !LauncherPreferences.ServerIsLocal;
            AjusteServidorLocal.Classes.Set("on", !_ajusteRemoto);
            AjusteServidorRemoto.Classes.Set("on", _ajusteRemoto);
            CampoServidorAjustes.IsVisible = _ajusteRemoto;
            if (string.IsNullOrEmpty(CampoServidorAjustes.Text))
                CampoServidorAjustes.Text = _ajusteRemoto ? LauncherPreferences.ServerHost : "";

            AjustesInstalacion.IsVisible = Program.Installation != null;
            if (Program.Installation is not { } layout) return;
            var state = layout.Load();
            TextoVersionInstalada.Text = SetupTexts.InstalledVersion(_idioma, state.Version, layout.Root);
            if (string.IsNullOrEmpty(CampoRepositorioAjustes.Text)) CampoRepositorioAjustes.Text = state.Repository;
        }

        private void AlElegirServidorAjustes(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _ajusteRemoto = (remitente as Button)?.Tag as string == "remoto";
            AjusteServidorLocal.Classes.Set("on", !_ajusteRemoto);
            AjusteServidorRemoto.Classes.Set("on", _ajusteRemoto);
            CampoServidorAjustes.IsVisible = _ajusteRemoto;
        }

        private async void AlGuardarServidor(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
        {
            string donde = _ajusteRemoto ? (CampoServidorAjustes.Text ?? "").Trim() : "";
            if (_ajusteRemoto && donde.Length == 0) { Avisar(SetupTexts.BadAddress(_idioma)); return; }
            QuitarElAviso();
            string antes = LauncherPreferences.ServerHost;
            bool eraLocal = LauncherPreferences.ServerIsLocal, seraLocal = LauncherPreferences.IsThisMachine(donde);
            if ((seraLocal && eraLocal) || donde.Equals(antes, StringComparison.OrdinalIgnoreCase))
            {
                // Nothing to restart for, but the press is answered: 127.0.0.1 or localhost as
                // "another machine" is this one.
                Avisar(SetupTexts.ConnectionUnchanged(_idioma, seraLocal), bueno: true);
                return;
            }

            // Going to another machine: the relay takes the client's ports on this one, which a
            // server here holds. That server is closed first -- asked, as an update asks.
            if (eraLocal && !seraLocal && await Task.Run(Network.ControlClient.ServidorVivo))
            {
                bool seguir = await Dialogs.ConfirmAsync(this, Title ?? "Jondo", SetupTexts.LocalServerStops(_idioma, donde),
                                                         SetupTexts.Save(_idioma), SetupTexts.Cancel(_idioma),
                                                         peligro: false);
                if (!seguir) return;
                GuardarServidor.IsEnabled = false;
                await Task.Run(async () =>
                {
                    if (Program.Installation is { } layout && Updater.ServersOf(layout.ServerExecutable).Count > 0)
                        await Updater.StopServerAsync(layout, CancellationToken.None);
                    else
                        Contract.RequestStop("JondoEmuServidor");
                    var tope = DateTime.Now.AddMinutes(1);
                    while (Network.ControlClient.ServidorVivo() && DateTime.Now < tope) await Task.Delay(300);
                });
            }
            LauncherPreferences.ServerHost = donde;

            // The relay and the local server are decided on starting: the launcher starts again.
            await Dialogs.ShowAsync(this, Title ?? "Jondo", SetupTexts.ConnectionSaved(_idioma), _textos.DialogAccept);
            Program.StartAgain(Environment.ProcessPath!);
            Close();
        }

        private void AlGuardarRepositorio(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (Program.Installation is not { } layout) return;
            string? repositorio = ReleaseFeed.Normalize(CampoRepositorioAjustes.Text);
            if (repositorio == null) { Avisar(SetupTexts.BadRepository(_idioma)); return; }
            QuitarElAviso();
            var state = layout.Load();
            state.Repository = repositorio;
            layout.Save(state);
            CampoRepositorioAjustes.Text = repositorio;
            _ = BuscarActualizacionAsync(manual: true);
        }

        private void AlBuscarActualizaciones(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
            => _ = BuscarActualizacionAsync(manual: true);

        private void AlRepararInstalacion(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
            => _ = RepararAsync(automatica: false);

        private void AlPulsarDesinstalar(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
            => _ = DesinstalarAsync(desdeWindows: false);

        /// <summary>Asks, and hands the uninstall to the copy that does it once this window has closed.</summary>
        private async Task DesinstalarAsync(bool desdeWindows)
        {
            if (Program.Installation is not { } layout) { if (desdeWindows) Close(); return; }
            var state = layout.Load();
            string pregunta = SetupTexts.UninstallQuestion(_idioma, layout.Root)
                              + (state.ClientDownloaded ? " " + SetupTexts.UninstallClientToo(_idioma) : "");
            bool si = await Dialogs.ConfirmAsync(this, SetupTexts.UninstallTitle(_idioma), pregunta,
                                                 SetupTexts.Uninstall(_idioma), SetupTexts.Cancel(_idioma));
            if (si && !Uninstaller.Start(layout))
            {
                Avisar(SetupTexts.ElevationRefused(_idioma));
                if (!desdeWindows) return;
            }
            if (si || desdeWindows) Close();
        }
    }
}
