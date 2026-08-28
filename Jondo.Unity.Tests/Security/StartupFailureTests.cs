using System;
using System.IO;
using Jondo.Unity.Server;
using Xunit;

namespace Jondo.Unity.Tests.Security
{
    /// <summary>
    /// What is left behind when the server dies on the way up.
    /// </summary>
    /// <remarks>
    /// It is a WinExe, so there is no console to read: a startup that throws used to close the
    /// window and leave nothing at all, and "it does not start" was the entire bug report. The
    /// point of the change is that the exception reaches <c>logs/debug.log</c>, so that is what is
    /// tested rather than only the sentence it is wrapped in.
    /// </remarks>
    public class StartupFailureTests
    {
        [Fact]
        public void Fatal_startup_log_keeps_the_exception_type_and_message()
        {
            string log = Program.StartupFailure(
                new InvalidOperationException("SQLite Error 8: attempt to write a readonly database"));

            Assert.Contains(nameof(InvalidOperationException), log);
            Assert.Contains("SQLite Error 8", log);
            Assert.Contains("readonly database", log);
        }

        [Fact]
        public void The_failure_really_reaches_the_file_and_not_just_the_screen()
        {
            // The half that matters and that the sentence alone does not prove. LogFile decides
            // where to write from a callback, so a test can point one at a temporary file and read
            // it back — and if that ever stops working, the message is written to a console that
            // a WinExe does not have and the failure is invisible again.
            string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".log");
            var log = new LogFile(() => path);

            try
            {
                log.WriteLine(Program.StartupFailure(new InvalidOperationException("no arranca")));
                // Sin Flush: LogFile abre con AutoFlush, cada linea va al disco al escribirla.

                Assert.True(File.Exists(path), "el fallo de arranque no ha llegado a ningún fichero");

                // Compartiendo, y no con File.ReadAllText: LogFile deja el fichero abierto con
                // FileShare.ReadWrite mientras el servidor vive, así que abrirlo en exclusiva —que
                // es lo que hace ReadAllText— falla con «lo está usando otro proceso». Es lo mismo
                // que le pasa a quien intenta mirar logs/debug.log con el servidor levantado.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                  FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                string written = reader.ReadToEnd();
                Assert.Contains("Fatal error while starting Jondo Server", written);
                Assert.Contains("no arranca", written);
            }
            finally
            {
                try { File.Delete(path); } catch (IOException) { }
            }
        }
    }
}
