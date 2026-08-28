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
    /// <remarks>
    /// It also rotates now, which it did not. The traffic log had reached 112 MB on this machine
    /// with nothing in the code that would ever have stopped it: two writes per frame, a full hex
    /// dump each, and no cap anywhere. That is a disk filling up on its own, and on a disk that
    /// fills up the server stops -- so an unbounded debug log is not only a mess, it is a way to
    /// take the server down by playing on it for long enough.
    ///
    /// Rotation rather than truncation because the reason to keep this file is to look at what just
    /// happened, and truncating throws away the recent half as readily as the old one.
    /// </remarks>
    public sealed class LogFile
    {
        /// <summary>Where one file stops and the next begins.</summary>
        /// <remarks>
        /// 32 MB is about forty minutes of a busy session at the size these lines run to: long
        /// enough that the file you want is nearly always the live one, small enough to open in an
        /// editor when it is not.
        /// </remarks>
        public const long MaxBytes = 32L * 1024 * 1024;

        /// <summary>How many rotated files to keep behind the live one.</summary>
        public const int Keep = 3;

        private readonly object _candado = new object();
        private readonly Func<string> _comoSeLlama;
        private StreamWriter? _escritor;
        private bool _seRindio;
        private long _escrito;

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
                    Cuenta(texto.Length + Environment.NewLine.Length);
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
                    Cuenta(texto.Length);
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

            // Append puts us at the end, so this is what was already there from earlier runs. Not
            // counting it would mean a server restarted every ten minutes never rotates at all.
            _escrito = flujo.Length;

            return new StreamWriter(flujo, new UTF8Encoding(false)) { AutoFlush = true };
        }

        /// <summary>
        /// Adds what was just written and rolls the file over once it is big enough.
        /// </summary>
        /// <remarks>
        /// Characters, not encoded bytes: this is a threshold, not an accounting, and asking the
        /// stream for its real length is a syscall that would run twice per frame forever. UTF-8
        /// undercounts on accents, so the file lands slightly over the cap. That is fine.
        ///
        /// Always called with the lock held.
        /// </remarks>
        private void Cuenta(int caracteres)
        {
            _escrito += caracteres;
            if (_escrito < MaxBytes) return;

            string vivo = _comoSeLlama();
            bool rotado = false;
            try
            {
                _escritor?.Dispose();
                _escritor = null;

                // Oldest first, or each move would land on top of the one after it.
                string ultimo = vivo + "." + Keep;
                if (File.Exists(ultimo)) File.Delete(ultimo);

                for (int i = Keep - 1; i >= 1; i--)
                {
                    string de = vivo + "." + i;
                    if (File.Exists(de)) File.Move(de, vivo + "." + (i + 1));
                }

                if (File.Exists(vivo)) File.Move(vivo, vivo + ".1");
                rotado = true;
            }
            catch (Exception)
            {
                // Something has the file open -- an editor, a tail, the reader in the Studio. Not a
                // reason to stop logging.
            }

            try { _escritor = Abrir(); } catch (Exception) { _seRindio = true; return; }

            // Abrir sets _escrito from the file it just opened, which is the right answer when the
            // rename worked -- a fresh file, zero bytes. When it did not, that same line hands back
            // the 32 MB we started from, and then EVERY line after this one would try to rotate
            // again and fail again. So the counter is reset by hand and the next attempt comes a
            // whole cap later, which is a backoff rather than a spin.
            if (!rotado) _escrito = 0;
        }
    }
}
