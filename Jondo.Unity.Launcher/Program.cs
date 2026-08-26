using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jondo.Unity.Launcher
{
    /// <summary>
    /// El lanzador: una ventana y nada más.
    ///
    /// Es el ejecutable que se reparte a los jugadores, así que lo que NO lleva dentro importa
    /// tanto como lo que lleva: ni base de datos, ni mapas, ni manejadores de protocolo, ni
    /// catálogo de efectos. Sólo la interfaz, el arranque del cliente de Dofus y el cliente del
    /// canal de mando.
    ///
    /// Y sobre todo: cerrarlo no apaga nada. El servidor es otro proceso, con su propia vida y con
    /// los jugadores que tenga dentro. Antes esto era el mismo programa y cerrar la ventana
    /// completaba un TaskCompletionSource del que colgaban los cinco servicios.
    /// </summary>
    internal static class Program
    {
        /// <summary>Cómo se llama el ejecutable del servidor, que vive al lado.</summary>
        public const string EjecutableDelServidor = "Jondo Server.exe";

        [STAThread]
        private static async Task Main()
        {
            if (!Contract.CogerElSitio("JondoEmuLanzador"))
            {
                try
                {
                    System.Windows.Forms.MessageBox.Show(
                        "El lanzador de Jondo ya está abierto.", "Jondo",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Information);
                }
                catch { }
                return;
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
                        System.Windows.Forms.MessageBox.Show(
                            UI.LauncherPreferences.Textos.GenericError + "\n\n" + ex.Message,
                            "Jondo",
                            System.Windows.Forms.MessageBoxButtons.OK,
                            System.Windows.Forms.MessageBoxIcon.Error);
                        return;
                    }
                    Console.WriteLine($"[Lanzador] Relé local activo hacia " +
                                      $"{UI.LauncherPreferences.ServerHost}.");
                }

                // Only when the server is this machine's. In remote mode the relay is already
                // listening on 5555, 6337, 8888 and 15881, so starting a local server here has
                // the two of them fighting for the same four ports: whichever binds second fails,
                // and which one that is depends on timing.
                if (UI.LauncherPreferences.ServerIsLocal) await AsegurarQueHayServidor();

                UI.LauncherWindow.OpenOnDedicatedThread();
                await _cerrada.Task;
                Console.WriteLine("[Lanzador] Ventana cerrada. El servidor sigue en marcha.");
            }
            finally
            {
                Network.RemoteRelay.Stop();
                Contract.SoltarElSitio();
            }
        }

        /// <summary>
        /// Si no hay servidor escuchando, arranca el de al lado y espera a que conteste.
        ///
        /// Esperar importa: el mod del cliente decide UNA sola vez, al inicializarse, si redirige al
        /// emulador, sondeando el puerto de mando con 100 ms de paciencia. Si en ese instante no hay
        /// nadie, el cliente no da ningún error —se conecta a los servidores de Ankama—, así que más
        /// vale que la ventana no se abra hasta que haya alguien al otro lado.
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
                // Que no esté no es un error del que haya que morirse: un jugador con sólo el
                // lanzador es el caso normal el día que el servidor esté en otra máquina. La
                // ventana ya sabe enseñar «fuera de línea» y dejar los botones apagados.
                Console.WriteLine($"[Lanzador] No hay {EjecutableDelServidor} al lado y no responde ninguno.");
                return;
            }

            Console.WriteLine("[Lanzador] No hay servidor escuchando; arrancando el de al lado.");
            try
            {
                // Suelto de verdad: lo arranca Windows, sin heredar la consola ni los descriptores
                // del lanzador, así que cerrar el lanzador después no se lo lleva por delante.
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

            // Con paciencia: el servidor lee la base, los managers y los mapas antes de abrir un
            // solo puerto, y eso son varios segundos en frío.
            if (!await Task.Run(() => Network.ControlClient.EsperarAlServidor(TimeSpan.FromSeconds(90))))
            {
                Console.WriteLine("[Lanzador] El servidor no ha llegado a contestar.");
            }
        }

        // ─── El cierre ──────────────────────────────────────────────────────────────────────

        private static readonly TaskCompletionSource _cerrada =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        private static int _yaPedido;

        /// <summary>
        /// La ventana avisa de que se ha cerrado. Ya no apaga nada: sólo termina este proceso.
        ///
        /// Aquí estaba el cable. Esto llamaba a un RequestShutdown que paraba los cinco servicios,
        /// así que cerrar la ventana echaba del juego a todo el que estuviera dentro.
        /// </summary>
        public static void RequestShutdown(string motivo)
        {
            if (Interlocked.Exchange(ref _yaPedido, 1) != 0) return;
            Console.WriteLine($"[Lanzador] Cerrando el lanzador ({motivo}).");
            _cerrada.TrySetResult();
        }

        /// <summary>El registro de depuración del lanzador, que es corto y va a su consola.</summary>
        public static void LogDebug(string mensaje)
            => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {mensaje}");
    }
}
