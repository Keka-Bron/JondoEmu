using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;

namespace Jondo.Unity.Launcher
{
    /// <summary>
    /// The launcher: a window and nothing else.
    ///
    /// It is the executable handed out to the players, so what it does NOT carry inside matters
    /// as much as what it carries: no database, no maps, no protocol handlers, no
    /// effects catalogue. Only the interface, starting the Dofus client and the
    /// control channel's client.
    ///
    /// And above all: closing it shuts nothing down. The server is another process, with its own life and with
    /// whatever players it has inside.
    /// </summary>
    /// <remarks>
    /// It no longer opens the window on a separate thread. With Windows Forms a thread of its own was needed, STA,
    /// so that the message loop did not get tangled with the asynchronous start; Avalonia has its
    /// own loop and the right thing is to give it the main thread. What there was before opening it
    /// —checking there is no other launcher, raising the relay if the server is remote and starting
    /// the server alongside if needed— stays the same and still goes first.
    /// </remarks>
    internal static class Program
    {
        /// <summary>What the server's executable, which lives alongside, is called.</summary>
        public static string EjecutableDelServidor => OperatingSystem.IsWindows() ? "Jondo Server.exe" : "Jondo Server";

        /// <summary>What the launcher is this time, from what is beside it.</summary>
        internal enum LaunchMode
        {
            /// <summary>Nothing beside it: it is the downloaded setup, and the window installs.</summary>
            Setup,

            /// <summary>An install.json beside it: an installation, kept up to date from its repository.</summary>
            Installed,

            /// <summary>The emulator's folder without install.json: unpacked by hand or a developer's checkout. Never updated.</summary>
            Portable,
        }

        internal static LaunchMode Mode { get; private set; }

        /// <summary>The installation this launcher belongs to, when it is <see cref="LaunchMode.Installed"/>.</summary>
        internal static Setup.InstallLayout? Installation { get; private set; }

        /// <summary>The release it was just updated to, when it was started again for that.</summary>
        internal static string JustUpdatedTo { get; private set; } = "";

        /// <summary>Started by Windows' installed apps to uninstall: the window only asks and hands it over.</summary>
        internal static bool UninstallRequested { get; private set; }

        /// <summary>Why the relay to the remote server could not be set up, for the window to say; empty when it was.</summary>
        internal static string RelayError { get; private set; } = "";

        [STAThread]
        private static int Main(string[] args)
        {
            if (args is ["--prepare-client-mod-build", var clientDirectory])
                return Setup.ClientModBuild.PrepareAsync(clientDirectory).GetAwaiter().GetResult();

            // The part of the setup that needs an administrator: a process of its own, no window.
            if (Array.IndexOf(args, "--elevated-setup") >= 0) return Setup.SystemIntegration.ElevatedMain(args);
            if (Array.IndexOf(args, "--uninstall-run") >= 0) return Setup.Uninstaller.RunMain(args);

            // Started again by the one before it (after an update, after the setup): it waits for that one to be gone.
            WaitForThePreviousOne(args);

            Installation = Setup.InstallLayout.Current;
            string here = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
            Mode = Array.IndexOf(args, "--setup") >= 0 ? LaunchMode.Setup
                 : Installation != null ? LaunchMode.Installed
                 : File.Exists(Path.Combine(here, Setup.InstallLayout.RootMarker)) || Environment.GetEnvironmentVariable("JONDO_EMULATOR_ROOT") is { Length: > 0 }
                     ? LaunchMode.Portable
                     : LaunchMode.Setup;
            int updated = Array.IndexOf(args, "--updated");
            if (updated >= 0 && updated + 1 < args.Length) JustUpdatedTo = args[updated + 1];
            if (Installation != null) Setup.InstallLayout.RemoveLeftovers(Installation.Root);

            if (Array.IndexOf(args, "--uninstall") >= 0)
            {
                if (Installation == null) return 1;
                UninstallRequested = true;
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
                return 0;
            }

            if (!Contract.CogerElSitio("JondoEmuLanzador"))
            {
                // And it is brought to the front, which is what whoever has just double-clicked expects.
                // Writing to a console that does not exist -- this is a WinExe -- was the same as
                // closing without a word.
                bool traido = LauncherActivation.Notify() || ElQueYaEstaba.PonerloDelante();
                Console.WriteLine(traido
                    ? "[Launcher] One was already open; it is brought to the front."
                    : "[Launcher] A Jondo launcher is already open.");
                return 0;
            }

            try
            {
                using var activation = new LauncherActivation();
                Console.WriteLine($"[Launcher] {Mode}{(Installation != null ? " at " + Installation.Root : "")}.");
                if (Mode == LaunchMode.Setup)
                {
                    // Nothing to connect to yet: the window installs first.
                    BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
                    return 0;
                }

                if (!UI.LauncherPreferences.ServerIsLocal)
                {
                    try
                    {
                        Network.RemoteRelay.Start(UI.LauncherPreferences.ServerHost);
                    }
                    catch (Exception ex)
                    {
                        // The window still opens and says why -- ports held by a server on this
                        // machine, most often. Closing without a word left the player with nothing.
                        Console.WriteLine($"[Launcher] Could not set up the relay to " +
                                          $"{UI.LauncherPreferences.ServerHost}: {ex.Message}");
                        RelayError = ex.Message;
                    }
                    Console.WriteLine($"[Launcher] Local relay active to " +
                                      $"{UI.LauncherPreferences.ServerHost}.");
                }

                // Only when the server is this machine's. In remote mode the relay is already
                // listening on the client's loopback ports, so starting a local server here
                // leaves both fighting over those ports: whichever binds second
                // fails, and which of the two it is depends on the clock.
                if (UI.LauncherPreferences.ServerIsLocal) AsegurarQueHayServidor().GetAwaiter().GetResult();

                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
                Console.WriteLine("[Launcher] Window closed. The server keeps running.");
                return 0;
            }
            finally
            {
                Network.RemoteRelay.Stop();
                Contract.SoltarElSitio();
            }
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()

                .LogToTrace();

        /// <summary>
        /// If no server is listening, starts the one alongside and waits for it to answer.
        ///
        /// Waiting matters: the client mod decides ONLY once, on initialising, whether to redirect to the
        /// emulator, probing the control port with 100 ms of patience. If at that instant nobody is
        /// there, the client gives no error —it connects to Ankama's servers—, so the window had better
        /// not open until there is someone on the other side.
        /// </summary>
        private static async Task AsegurarQueHayServidor()
        {
            if (Network.ControlClient.ServidorVivo())
            {
                Console.WriteLine("[Launcher] There is a server running; attaching to it.");
                return;
            }

            string? aquí = Path.GetDirectoryName(Environment.ProcessPath ?? "");
            string servidor = Path.Combine(aquí ?? "", EjecutableDelServidor);
            if (!File.Exists(servidor)) servidor = Path.Combine(Paths.Root, EjecutableDelServidor);
            if (!File.Exists(servidor))
            {
                // That it is not there is not an error to die of: a player with only the
                // launcher is the normal case the day the server is on another machine. The
                // window already knows how to show «fuera de línea» and leave the buttons greyed out.
                Console.WriteLine($"[Launcher] There is no {EjecutableDelServidor} alongside and none answers.");
                return;
            }

            Console.WriteLine("[Launcher] No server listening; starting the one alongside.");
            try
            {
                if (!await StartServerAsync(servidor, aquí ?? "")) Console.WriteLine("[Launcher] The server never answered.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Launcher] Could not start the server: {ex.Message}");
            }
        }

        /// <summary>
        /// Starts a server and waits for it to answer: false when it never does. The setup's last
        /// step uses it too, so the launcher it opens finds the server already there.
        /// </summary>
        internal static async Task<bool> StartServerAsync(string executable, string workingDirectory)
        {
            // Truly detached: the system starts it, without inheriting the launcher's console nor
            // descriptors, so closing the launcher afterwards does not take it down with it.
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true,
            });

            // With patience: the server reads the base, the managers and the maps before opening a
            // single port, and that is several seconds from cold.
            return await Task.Run(() => Network.ControlClient.EsperarAlServidor(Network.ControlClient.PlazoDeArranque));
        }

        /// <summary>
        /// With <c>--after PID</c>, waits up to half a minute for that process to end: the launcher
        /// that started this one (the setup, or itself before an update) still holds the single
        /// instance and the files.
        /// </summary>
        private static void WaitForThePreviousOne(string[] args)
        {
            int at = Array.IndexOf(args, "--after");
            if (at < 0 || at + 1 >= args.Length || !int.TryParse(args[at + 1], out int pid)) return;
            try
            {
                using var previous = Process.GetProcessById(pid);
                previous.WaitForExit(30_000);
            }
            catch (ArgumentException) { }          // already gone
            catch (InvalidOperationException) { }
        }

        /// <summary>
        /// Starts another launcher -- the installed one after the setup, the new one after an
        /// update -- that waits for this one to end; then this one closes.
        /// </summary>
        internal static void StartAgain(string executable, params string[] extra)
        {
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(executable) ?? "",
                UseShellExecute = false,
            };
            start.ArgumentList.Add("--after");
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (string argument in extra) start.ArgumentList.Add(argument);
            // The setup's own JONDO_EMULATOR_ROOT, if any, must not follow it into the installation.
            start.Environment.Remove("JONDO_EMULATOR_ROOT");
            Process.Start(start);
            Console.WriteLine($"[Launcher] Starting {executable} again; this one closes.");
        }

        // ─── The closing ────────────────────────────────────────────────────────────────────

        private static int _yaPedido;

        /// <summary>
        /// The window reports that it has closed. It no longer shuts anything down.
        ///
        /// Here was the wire. This called a RequestShutdown that stopped the five services,
        /// so closing the window threw everyone who was inside out of the game.
        /// </summary>
        public static void RequestShutdown(string motivo)
        {
            if (Interlocked.Exchange(ref _yaPedido, 1) != 0) return;
            Console.WriteLine($"[Launcher] Closing the launcher ({motivo}).");
        }

        /// <summary>The launcher's debug log, which is short and goes to its console.</summary>
        public static void LogDebug(string mensaje)
            => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {mensaje}");
    }
}
