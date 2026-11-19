using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Jondo.Unity.Launcher.Packs;
using Jondo.Unity.Launcher.Setup;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// The client block's download from Ankama's CDN: the Windows or the Linux client, fetched
    /// whole, or the one in use checked and completed.
    /// </summary>
    /// <remarks>
    /// The same work the installer does for a first install, for a player who already has the
    /// launcher: a client that went missing, one that is incomplete, the other platform's client
    /// on Linux (native, or Windows through Wine), or the Linux client fetched on Windows to be
    /// taken to another computer. Each platform's client keeps a folder of its own: their
    /// executable parts write different files under the same names.
    /// </remarks>
    public sealed partial class MainWindow
    {
        /// <summary>The platform chosen in the block; empty until the player picks one.</summary>
        private string _plataformaCdn = "";

        /// <summary>The chosen platform, or the client in use's, or this machine's.</summary>
        private string PlataformaCdn
        {
            get
            {
                if (_plataformaCdn.Length > 0) return _plataformaCdn;
                string actual = LauncherService.ResolveClient();
                return actual.Length > 0 && ClientCdn.RunsHere(ClientCdn.PlatformOf(actual))
                    ? ClientCdn.PlatformOf(actual) : ClientCdn.HostPlatform;
            }
        }

        /// <summary>What the button will do: which version, in which folder (empty: to be asked), and whether a client is already there.</summary>
        private readonly record struct PlanCdn(string Version, string Folder, bool Verifies);

        private static PlanCdn PlanearCdn(string plataforma)
        {
            string version = ClientDownload.Version;
            // A client stored for another computer goes where the player says.
            if (!ClientCdn.RunsHere(plataforma)) return new PlanCdn(version, "", false);

            string actual = LauncherService.ResolveClient();
            if (actual.Length > 0 && ClientCdn.PlatformOf(actual) == plataforma)
            {
                // The client in use, checked against its own version's files.
                string carpeta = Path.GetDirectoryName(Path.GetFullPath(actual))!;
                string? suya = ClientVersion.Read(carpeta);
                return new PlanCdn(suya is "3.6.10.11" or "3.6.10.10" ? suya : version, carpeta, true);
            }

            // Another platform's client, or none: beside the installation's, or beside the one in use.
            string? padre = Program.Installation?.Root
                            ?? (actual.Length > 0 ? Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(actual))) : null);
            if (padre == null) return new PlanCdn(version, "", false);
            string destino = Path.Combine(padre, ClientCdn.FolderName(version, plataforma));
            return new PlanCdn(version, destino, File.Exists(Path.Combine(destino, ClientCdn.ExecutableFor(plataforma))));
        }

        private void RefrescarCdn()
        {
            RotuloCdn.Text = SetupTexts.CdnTitle(_idioma);
            TextoAjusteClienteWindows.Text = SetupTexts.PlatformChoice(ClientCdn.Windows);
            TextoAjusteClienteLinux.Text = SetupTexts.PlatformChoice(ClientCdn.Linux);

            string plataforma = PlataformaCdn;
            AjusteClienteWindows.Classes.Set("on", plataforma == ClientCdn.Windows);
            AjusteClienteLinux.Classes.Set("on", plataforma == ClientCdn.Linux);

            var plan = PlanearCdn(plataforma);
            string nota = SetupTexts.CdnNote(_idioma, plataforma, plan.Version, plan.Verifies, plan.Folder);
            string queEs = SetupTexts.PlatformNote(_idioma, plataforma);
            PieCdn.Text = queEs.Length > 0 ? queEs + " " + nota : nota;
            TextoBotonCdn.Text = plan.Verifies ? SetupTexts.CdnVerify(_idioma) : SetupTexts.CdnDownload(_idioma);
            IconoCdn.Kind = plan.Verifies ? "check" : "download";
            BotonCdn.IsEnabled = !_trabajando;
        }

        private void AlElegirPlataformaCdn(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _plataformaCdn = (remitente as Button)?.Tag as string ?? ClientCdn.HostPlatform;
            RefrescarCdn();
        }

        private async void AlPulsarCdn(object? remitente, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_trabajando) return;
            if (_packEnMarcha != null) { Avisar(SetupTexts.CdnBusy(_idioma)); return; }
            QuitarElAviso();

            string plataforma = PlataformaCdn;
            var plan = PlanearCdn(plataforma);
            if (plan.Folder.Length == 0)
            {
                string? elegida = await PedirCarpetaDelCdnAsync(plataforma, plan.Version);
                if (elegida == null) return;
                plan = plan with
                {
                    Folder = elegida,
                    Verifies = File.Exists(Path.Combine(elegida, ClientCdn.ExecutableFor(plataforma))),
                };
            }
            await DescargarDelCdnAsync(plataforma, plan);
        }

        /// <summary>
        /// Where the player wants it: the folder itself when it is empty or already holds that
        /// platform's client, else a folder of its own inside.
        /// </summary>
        private async Task<string?> PedirCarpetaDelCdnAsync(string plataforma, string version)
        {
            var elegidas = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = SetupTexts.CdnWhere(_idioma, plataforma),
                AllowMultiple = false,
            });
            string? ruta = elegidas.Count > 0 ? elegidas[0].TryGetLocalPath() : null;
            if (string.IsNullOrEmpty(ruta)) return null;
            ruta = Path.GetFullPath(ruta);
            bool vacia = !Directory.Exists(ruta) || !Directory.EnumerateFileSystemEntries(ruta).Any();
            bool suya = ClientCdn.PlatformIn(ruta) == plataforma;
            return vacia || suya ? ruta : Path.Combine(ruta, ClientCdn.FolderName(version, plataforma));
        }

        private async Task DescargarDelCdnAsync(string plataforma, PlanCdn plan)
        {
            if (_trabajando) return;
            var paso = new SetupStep("client-download",
                                     plan.Verifies ? SetupTexts.StepClient(_idioma) : SetupTexts.StepClientDownload(_idioma),
                                     1, TimeSpan.FromMinutes(plan.Verifies ? 10 : 40));
            EmpezarTrabajo(plan.Verifies ? SetupTexts.CdnVerifying(_idioma, plataforma) : SetupTexts.CdnDownloading(_idioma, plataforma),
                           new List<SetupStep> { paso }, () => paso.Fraction, () => paso.Remaining);
            _alReintentar = () => _ = DescargarDelCdnAsync(plataforma, plan);
            RefrescarCdn();

            var cancelar = _cancelarTrabajo!.Token;
            string ejecutable;
            try
            {
                ejecutable = await Task.Run(async () =>
                {
                    paso.Start(SetupTexts.FromAnkama(_idioma, plan.Version, plataforma));
                    try
                    {
                        if (!await ClientCdn.IsServedAsync(Http.Shared, plan.Version, plataforma, cancelar))
                            throw new SetupException(SetupTexts.ClientNotOnCdn(_idioma, plan.Version, plataforma));

                        // Only a client that is not there yet has to fit: a check rewrites what is damaged, in place.
                        if (!plan.Verifies)
                        {
                            var manifiesto = await ClientCdn.ManifestAsync(Http.Shared, plan.Version, plataforma, null, cancelar);
                            long falta = ClientCdn.SpaceNeeded(ClientCdn.Size(manifiesto));
                            long libre = SafeFreeSpace(Directory.Exists(plan.Folder) ? plan.Folder : Path.GetDirectoryName(plan.Folder) ?? plan.Folder);
                            if (libre < falta)
                                throw new SetupException(SetupTexts.NotEnoughSpace(_idioma, Format.Bytes(falta), Format.Bytes(libre)));
                        }

                        var leyendo = new Progress<PackProgress>(p => paso.Report(0, SetupTexts.ReadingFileList(_idioma, p.Fraction)));
                        string hecho = await ClientCdn.FetchAsync(Http.Shared, plan.Version, plataforma, plan.Folder, leyendo,
                                                                  Installer.FromCdn(paso, _idioma), cancelar);
                        paso.Finish(plan.Folder);
                        return hecho;
                    }
                    catch (OperationCanceledException) { paso.Fail("—"); throw; }
                    catch (Exception ex) { paso.Fail(ex.Message); throw; }
                });
            }
            catch (Exception ex) when (ex is SetupException or PackException or OperationCanceledException or IOException
                                           or UnauthorizedAccessException or System.Net.Http.HttpRequestException)
            {
                FallarTrabajo(ex);
                RefrescarCdn();
                return;
            }

            // A client this machine plays becomes the launcher's; one for another computer stays where it is.
            bool usar = ClientCdn.RunsHere(plataforma);
            bool faltanMods = false;
            if (usar)
            {
                LauncherPreferences.SetClientExecutable(ejecutable);
                if (Program.Installation is { } layout)
                {
                    var state = layout.Load();
                    state.Client = ejecutable;
                    if (Path.GetFullPath(plan.Folder).StartsWith(Path.GetFullPath(layout.Root) + Path.DirectorySeparatorChar,
                                                                  ClientInstallation.PathComparison))
                        state.ClientDownloaded = true;
                    layout.Save(state);
                    // On Windows the mods live in the client's folder: a new one has none yet.
                    faltanMods = OperatingSystem.IsWindows() && !MelonLoaderSetup.IsInstalled(plan.Folder);
                }
            }

            string nota = SetupTexts.CdnReadyNote(_idioma, plan.Version, plataforma, plan.Folder, usar)
                          + (faltanMods ? Environment.NewLine + SetupTexts.CdnModsMissing(_idioma) : "");
            if (faltanMods)
                TerminarTrabajo(SetupTexts.CdnReady(_idioma), nota, SetupTexts.Repair(_idioma).ToUpperInvariant(),
                                () => _ = RepararAsync(automatica: false));
            else
                TerminarTrabajo(SetupTexts.CdnReady(_idioma), nota, SetupTexts.BackToLauncher(_idioma), () =>
                {
                    MostrarVista(Vista.Normal);
                    Recolocar();
                });
            _plataformaCdn = "";
            RefrescarIdiomasDelJuego();
            RefrescarRutaDelCliente();
            RefrescarPacks();
        }
    }
}
