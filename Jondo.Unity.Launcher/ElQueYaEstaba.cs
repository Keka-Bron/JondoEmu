using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Jondo.Unity.Launcher
{
    /// <summary>
    /// When there is already a launcher open: instead of doing nothing, it is brought to the front.
    /// </summary>
    /// <remarks>
    /// The launcher is handed out as a <c>WinExe</c>, that is without a console. When the place was taken,
    /// the process wrote «ya hay un lanzador abierto» and closed: on screen that is
    /// exactly nothing, and whoever has just double-clicked only sees that nothing happens. With the window
    /// of the first one hidden behind the browser, the result is «the launcher does not start».
    ///
    /// What anyone expects on opening a second time something already open is that it gets
    /// brought to the front, so that is what is done. Outside Windows it is not attempted —this is
    /// user32— and then indeed only the log line remains, which is the honest thing: there is nothing better
    /// to do without adding a dependency for a convenience.
    /// </remarks>
    internal static class ElQueYaEstaba
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr ventana);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr ventana, int como);

        /// <summary>SW_RESTORE: if it is minimised, it raises it without changing its size.</summary>
        private const int Restaurar = 9;

        /// <summary>Finds the launcher that was already there and brings it to the front. Returns whether it managed to.</summary>
        public static bool PonerloDelante()
        {
            if (!OperatingSystem.IsWindows()) return false;

            try
            {
                int yo = Environment.ProcessId;
                string nombre = Process.GetCurrentProcess().ProcessName;

                foreach (var otro in Process.GetProcessesByName(nombre))
                {
                    if (otro.Id == yo) continue;

                    IntPtr ventana = otro.MainWindowHandle;
                    if (ventana == IntPtr.Zero) continue;

                    ShowWindow(ventana, Restaurar);
                    return SetForegroundWindow(ventana);
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[Lanzador] No se ha podido traer al frente el que ya estaba: {ex.Message}");
            }

            return false;
        }
    }
}
