using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Jondo.Unity.Launcher
{
    /// <summary>
    /// Cuando ya hay un lanzador abierto: en vez de no hacer nada, se le pone delante.
    /// </summary>
    /// <remarks>
    /// El lanzador se reparte como <c>WinExe</c>, o sea sin consola. Cuando el sitio estaba cogido,
    /// el proceso escribía «ya hay un lanzador abierto» y se cerraba: en pantalla eso es
    /// exactamente nada, y quien acaba de hacer doble clic sólo ve que no pasa nada. Con la ventana
    /// del primero escondida detrás del navegador, el resultado es «el lanzador no arranca».
    ///
    /// Lo que espera cualquiera al abrir por segunda vez algo que ya está abierto es que se le
    /// ponga delante, así que eso es lo que se hace. Fuera de Windows no se intenta —esto es
    /// user32— y entonces sí queda sólo la línea de registro, que es lo honrado: no hay nada mejor
    /// que hacer sin meter una dependencia por una comodidad.
    /// </remarks>
    internal static class ElQueYaEstaba
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr ventana);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr ventana, int como);

        /// <summary>SW_RESTORE: si está minimizada, la levanta sin cambiarle el tamaño.</summary>
        private const int Restaurar = 9;

        /// <summary>Busca el lanzador que ya estaba y lo pone delante. Devuelve si lo consiguió.</summary>
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
