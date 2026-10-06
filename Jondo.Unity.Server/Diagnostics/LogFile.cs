using Jondo.Unity.Launcher;
using System;
using System.IO;
using System.Text;

namespace Jondo.Unity.Server
{
    /// <summary>
    /// A log file that stays open.
    ///
    /// All of this used to be written with File.AppendAllText, which opens the file, writes and
    /// closes it. On every line. And the traffic log writes TWICE per frame -- once per direction --
    /// so in a busy fight that is hundreds of opens per second, each with its round trip to the file
    /// system, plus a Directory.Exists per call because the path was resolved every time. The server
    /// spent more time opening files than serving the client.
    ///
    /// Here the handle is opened once and kept. What is NOT done is holding lines in a buffer to
    /// write them later: this is a debug log, so the time it is really needed is exactly the time
    /// the server dies, and an unflushed buffer takes with it precisely the last lines, which are the
    /// ones that explain what happened. With AutoFlush every line reaches the disk as before, but
    /// without opening and closing.
    ///
    /// The path is resolved ONCE, the first time. Paths.LogsDir checks and creates the folder on
    /// every call, and that does not have to be paid per line either.
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

        /// <summary>The debug log: what the emulator does, line by line.</summary>
        public static readonly LogFile Debug = new LogFile(() => Paths.DebugLog);

        /// <summary>El tráfico en crudo, hexadecimal incluido.</summary>
        public static readonly LogFile Traffic = new LogFile(() => Paths.TrafficLog);

        /// <summary>One JSON line per significant action of a player or an administrator.</summary>
        public static readonly LogFile Activity = new LogFile(() => Paths.ActivityLog);

        /// <summary>
        /// Writes a line. If it cannot be written -- disk full, file locked -- it stays quiet and does
        /// not try again: a log that cannot be written is no reason to bring the server down, and even
        /// less to try again with every frame.
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

        /// <summary>The same, without adding the line break.</summary>
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
