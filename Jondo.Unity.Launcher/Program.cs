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
        public const string EjecutableDelServidor = "Jondo Server.exe";

        [STAThread]
        private static int Main(string[] args)
        {
            if (!Contract.CogerElSitio("JondoEmuLanzador"))
            {
                // And it is brought to the front, which is what whoever has just double-clicked expects.
                // Writing to a console that does not exist -- this is a WinExe -- was the same as
                // closing without a word.
                bool traido = ElQueYaEstaba.PonerloDelante();
                Console.WriteLine(traido
                    ? "[Lanzador] Ya había uno abierto; se le pone delante."
                    : "[Lanzador] Ya hay un lanzador de Jondo abierto.");
                return 0;
            }

            try
            {
                if (!UI.LauncherPreferences.ServerIsLocal)
                {
                    try
                    {
                        Network.RemoteRelay.Start(UI.LauncherPreferences.ServerHost);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Lanzador] No se ha podido levantar el relé hacia " +
                                          $"{UI.LauncherPreferences.ServerHost}: {ex.Message}");
                        return 1;
                    }
                    Console.WriteLine($"[Lanzador] Relé local activo hacia " +
                                      $"{UI.LauncherPreferences.ServerHost}.");
                }

                // Only when the server is this machine's. In remote mode the relay is already
                // listening on 5555, 6337, 8888 and 15881, so starting a local server here
                // leaves both fighting over the same four ports: whichever binds second
                // fails, and which of the two it is depends on the clock.
                if (UI.LauncherPreferences.ServerIsLocal) AsegurarQueHayServidor().GetAwaiter().GetResult();

                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
                Console.WriteLine("[Lanzador] Ventana cerrada. El servidor sigue en marcha.");
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
                Console.WriteLine("[Lanzador] Hay un servidor en marcha; me engancho a él.");
                return;
            }

            string? aquí = Path.GetDirectoryName(Environment.ProcessPath ?? "");
            string servidor = Path.Combine(aquí ?? "", EjecutableDelServidor);
            if (!File.Exists(servidor))
            {
                // That it is not there is not an error to die of: a player with only the
                // launcher is the normal case the day the server is on another machine. The
                // window already knows how to show «fuera de línea» and leave the buttons greyed out.
                Console.WriteLine($"[Lanzador] No hay {EjecutableDelServidor} al lado y no responde ninguno.");
                return;
            }

            Console.WriteLine("[Lanzador] No hay servidor escuchando; arrancando el de al lado.");
            try
            {
                // Truly detached: the system starts it, without inheriting the launcher's console nor
                // descriptors, so closing the launcher afterwards does not take it down with
                // it.
                Process.Start(new ProcessStartInfo
                {
                    FileName = servidor,
                    WorkingDirectory = aquí ?? "",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Lanzador] No se ha podido arrancar el servidor: {ex.Message}");
                return;
            }

            // With patience: the server reads the base, the managers and the maps before opening a
            // single port, and that is several seconds from cold.
            if (!await Task.Run(() => Network.ControlClient.EsperarAlServidor(Network.ControlClient.PlazoDeArranque)))
            {
                Console.WriteLine("[Lanzador] El servidor no ha llegado a contestar.");
            }
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
            Console.WriteLine($"[Lanzador] Cerrando el lanzador ({motivo}).");
        }

        /// <summary>The launcher's debug log, which is short and goes to its console.</summary>
        public static void LogDebug(string mensaje)
            => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {mensaje}");
    }
}
