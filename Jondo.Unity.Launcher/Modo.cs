using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Jondo.Unity.Launcher
{
    /// <summary>
    /// En qué se convierte este ejecutable al arrancar: en el servidor o en el lanzador.
    ///
    /// Son dos procesos, no dos ejecutables. El mismo .exe arrancado dos veces con argumentos
    /// distintos ya son dos procesos, y así la raíz del emulador se queda como está —un .exe y
    /// carpetas, que es una regla escrita de este proyecto— sin tener que partir el ensamblado en
    /// una biblioteca y dos puntos de entrada.
    ///
    ///   Jondo Emulator Launcher.exe              el lanzador: la ventana, sin servicios
    ///   Jondo Emulator Launcher.exe --servidor   el servidor: los servicios, sin ventana
    ///
    /// Lo que se paga: el proceso servidor sigue cargando WinForms aunque no dibuje nada, y no
    /// puede correr como servicio de Windows ni sin sesión de escritorio. Para dos procesos en la
    /// misma máquina —que es lo que hay, porque el lanzador arranca el Dofus.exe y le maneja la
    /// ventana— no molesta.
    /// </summary>
    public static class Modo
    {
        /// <summary>El argumento que convierte el arranque en servidor.</summary>
        public const string Servidor = "--servidor";

        public static bool EsServidor(string[] args)
        {
            foreach (string arg in args)
            {
                if (string.Equals(arg, Servidor, StringComparison.OrdinalIgnoreCase)) return true;
                // Se admite el inglés porque es el que escribiría cualquiera que venga de fuera.
                if (string.Equals(arg, "--server", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(arg, "--headless", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ─── Uno de cada, y sólo uno ────────────────────────────────────────────────────────
        //
        // No había ningún guardia de instancia: dos servidores se peleaban por el 8888 y por la
        // tubería con nombre "15881", y el segundo se moría escribiendo el error en una consola que
        // en un WinExe no existe. Doble clic que no hacía nada y nadie sabía por qué.
        //
        // El candado es "Local\", no "Global\": es por sesión de Windows, que es lo que se quiere.

        private static Mutex? _candado;

        public static bool CogerElSitio(bool servidor)
        {
            string nombre = servidor ? @"Local\JondoEmuServidor" : @"Local\JondoEmuLanzador";
            try
            {
                _candado = new Mutex(initiallyOwned: true, nombre, out bool nuestro);
                if (!nuestro)
                {
                    _candado.Dispose();
                    _candado = null;
                }
                return nuestro;
            }
            catch
            {
                // Si el candado no se puede coger por lo que sea, mejor dejar arrancar que impedir
                // arrancar: el fallo de los puertos ya avisa después, y ahora avisa bien.
                return true;
            }
        }

        public static void SoltarElSitio()
        {
            try { _candado?.ReleaseMutex(); } catch { }
            _candado?.Dispose();
            _candado = null;
        }

        // ─── La consola del servidor ────────────────────────────────────────────────────────

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int processId);

        private const int ConsolaDelPadre = -1;

        /// <summary>
        /// Le da una consola al servidor.
        ///
        /// El ejecutable es un WinExe, así que no trae ninguna: sin esto, el servidor corriendo solo
        /// no se vería por ninguna parte y no habría dónde pulsar Ctrl+C. Con ella, el servidor es
        /// una ventana de consola que se ve, se lee y se cierra, que es lo que uno espera de un
        /// servidor.
        ///
        /// Se llama ANTES de ConsoleLogBuffer.Initialize, porque el buffer se queda con el
        /// Console.Out que haya en ese momento y lo reenvía; si la consola llega después, lo que se
        /// escriba no aparece en ella.
        /// </summary>
        public static void AbrirConsola()
        {
            try
            {
                // Si nos han arrancado desde una consola —una terminal, por ejemplo— se usa ésa en
                // vez de abrir otra encima.
                if (!AttachConsole(ConsolaDelPadre)) AllocConsole();

                var salida = new System.IO.StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                Console.SetOut(salida);
                Console.Title = "Jondo — servidor";
            }
            catch { }
        }

        // ─── Arrancar el servidor desde el lanzador ─────────────────────────────────────────

        /// <summary>
        /// Arranca este mismo ejecutable en modo servidor, suelto.
        ///
        /// Suelto de verdad: con UseShellExecute lo lanza el propio Windows, sin heredar la consola
        /// ni los descriptores del lanzador, así que cerrar el lanzador después no se lo lleva por
        /// delante. Que es exactamente lo que se busca.
        /// </summary>
        public static bool ArrancarServidor()
        {
            string? yo = Environment.ProcessPath;
            if (string.IsNullOrEmpty(yo)) return false;

            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = yo,
                    Arguments = Servidor,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(yo) ?? "",
                    UseShellExecute = true,
                };
                var proceso = Process.Start(info);
                return proceso != null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Lanzador] No se ha podido arrancar el servidor: {ex.Message}");
                return false;
            }
        }
    }
}
