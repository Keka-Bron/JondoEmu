using Jondo.Unity.Launcher;
using System;
using System.IO;
using System.Text;

namespace Jondo.Unity.Server
{
    /// <summary>
    /// Un fichero de registro que se queda abierto.
    ///
    /// Todo esto se escribía con File.AppendAllText, que abre el fichero, escribe y lo cierra. En
    /// cada línea. Y el registro de tráfico escribe DOS veces por trama —una por sentido— así que
    /// en un combate movido eso son cientos de aperturas por segundo, cada una con su ida al
    /// sistema de ficheros, más un Directory.Exists por llamada porque la ruta se resolvía cada
    /// vez. El servidor se pasaba más tiempo abriendo ficheros que atendiendo al cliente.
    ///
    /// Aquí el manejador se abre una vez y se queda. Lo que NO se hace es guardar las líneas en un
    /// buffer para escribirlas luego: esto es un registro de depuración, o sea que la vez que de
    /// verdad hace falta es justo la vez que el servidor se muere, y un buffer sin vaciar se lleva
    /// por delante precisamente las últimas líneas, que son las que explican qué pasó. Con
    /// AutoFlush queda en disco cada línea igual que antes, pero sin abrir y cerrar.
    ///
    /// La ruta se resuelve UNA vez, la primera. Paths.LogsDir comprueba y crea la carpeta en cada
    /// llamada, y eso tampoco tiene por qué pagarse por línea.
    /// </summary>
    public sealed class LogFile
    {
        private readonly object _candado = new object();
        private readonly Func<string> _comoSeLlama;
        private StreamWriter? _escritor;
        private bool _seRindio;

        public LogFile(Func<string> comoSeLlama) => _comoSeLlama = comoSeLlama;

        /// <summary>El registro de depuración: lo que hace el emulador, línea a línea.</summary>
        public static readonly LogFile Debug = new LogFile(() => Paths.DebugLog);

        /// <summary>El tráfico en crudo, hexadecimal incluido.</summary>
        public static readonly LogFile Traffic = new LogFile(() => Paths.TrafficLog);

        /// <summary>Una línea JSON por acción importante de un jugador o administrador.</summary>
        public static readonly LogFile Activity = new LogFile(() => Paths.ActivityLog);

        /// <summary>
        /// Escribe una línea. Si no se puede escribir —disco lleno, fichero bloqueado— se calla y
        /// no vuelve a intentarlo: un registro que no se puede escribir no es motivo para tirar el
        /// servidor, y menos aún para intentarlo otra vez con cada trama.
        /// </summary>
        public void WriteLine(string texto)
        {
            if (_seRindio) return;

            lock (_candado)
            {
                if (_seRindio) return;
                try
                {
                    _escritor ??= Abrir();
                    _escritor.WriteLine(texto);
                }
                catch (Exception)
                {
                    _seRindio = true;
                    try { _escritor?.Dispose(); } catch { }
                    _escritor = null;
                }
            }
        }

        /// <summary>Lo mismo, pero sin añadir el salto de línea.</summary>
        public void Write(string texto)
        {
            if (_seRindio) return;

            lock (_candado)
            {
                if (_seRindio) return;
                try
                {
                    _escritor ??= Abrir();
                    _escritor.Write(texto);
                }
                catch (Exception)
                {
                    _seRindio = true;
                    try { _escritor?.Dispose(); } catch { }
                    _escritor = null;
                }
            }
        }

        private StreamWriter Abrir()
        {
            var flujo = new FileStream(_comoSeLlama(), FileMode.Append, FileAccess.Write,
                                       FileShare.ReadWrite);
            return new StreamWriter(flujo, new UTF8Encoding(false)) { AutoFlush = true };
        }
    }
}
