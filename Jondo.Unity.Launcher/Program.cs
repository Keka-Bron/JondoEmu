using System;
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
                // El servidor vive fuera del equipo del jugador y su ciclo de vida no pertenece
                // al lanzador. La ventana se abre de inmediato; ella sondea el servidor sin
                // bloquear la interfaz y enseña el estado fuera de línea cuando no responde.
                UI.LauncherWindow.OpenOnDedicatedThread();
                await _cerrada.Task;
                Console.WriteLine("[Lanzador] Ventana cerrada. El servidor sigue en marcha.");
            }
            finally
            {
                Contract.SoltarElSitio();
            }

            // Y aquí se termina, a propósito y como última cosa.
            //
            // Volver de Main no basta: la ventana ya está cerrada y su hilo era de fondo, pero el
            // motor de audio (Media Foundation levanta hilos propios para la música, y no siempre
            // los baja) o cualquier otro hilo nativo que haya quedado vivo mantiene el proceso
            // abierto SIN ventana —sin nada que cerrar—, y la única forma de acabarlo era el
            // Administrador de tareas. Este proceso es una hoja: cerrada su ventana no le queda
            // nada que hacer, así que se apaga él mismo y con él cualquier hilo que haya quedado
            // enganchado.
            Environment.Exit(0);
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
