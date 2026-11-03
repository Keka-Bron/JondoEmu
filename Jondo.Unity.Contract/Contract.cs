using System;
using System.Threading;

namespace Jondo.Unity.Launcher
{
    /// <summary>
    /// The contract between the server and the launcher. It is the ONLY thing both know.
    ///
    /// The launcher is handed out to the players, so it cannot carry the server inside: neither the
    /// database, nor the maps, nor the protocol handlers, nor the effects catalogue.
    /// They are two real executables and this library is the only piece that travels in both.
    ///
    /// That is why it stays small: route names, process guards and the codes with which
    /// the server says what happened. Everything added to this file ends up on
    /// every player's computer, so it had better be little.
    /// </summary>
    public static class Contract
    {
        /// <summary>The version published in the status.</summary>
        public const string Version = "3.6.10.10";

        /// <summary>The source address when the request comes from this same machine.</summary>
        public const string LocalIp = "127.0.0.1";

        /// <summary>
        /// The port it is sent through.
        ///
        /// It is the HAAPI's on purpose: it is the one the client mod probes to decide whether to redirect
        /// to the emulator, so «this port answers» is exactly the sign of life the
        /// launcher needs before starting a client.
        /// </summary>
        public const int Puerto = 8888;

        /// <summary>The prefix of all the control routes.</summary>
        public const string Prefijo = "/api/";

        // ─── How many fit ───────────────────────────────────────────────────────────────────
        //
        // They are TWO different things and for a while they were the same number, which is what made
        // the whole server admit no more than eight connections:
        //
        //   * how many clients ONE person can have open at once. Eight, which is what fits
        //     in a Dofus party, and it is where the number came from: from the multi-account launcher.
        //   * how many players the server admits IN TOTAL, which has nothing to do with the
        //     above and which used to be eight by the accident of sharing a constant.
        //
        // The first is counted per address: whoever plays with several accounts does so from their
        // computer, so the IP is what groups one same person.

        /// <summary>Clients one same address can have open at once.</summary>
        public const int ClientesPorIp = 8;

        /// <summary>Connected players the server admits in total.</summary>
        public const int ClientesEnTotal = 500;

        // ─── The codes ──────────────────────────────────────────────────────────────────────
        //
        // The server says WHAT happened; the launcher decides HOW to tell the person and in which
        // language. If the server sent the ready-made sentence it would have to know the user's language, and
        // for that it would have to read a preferences file from someone's desktop.

        public const string MotivoSesionCaducada = "sesion-caducada";
        public const string MotivoCuentaYaAbierta = "cuenta-ya-abierta";
        public const string MotivoTopeDeClientes = "tope-de-clientes";

        // ─── One of each, and only one ──────────────────────────────────────────────────────
        //
        // There was no instance guard: two servers fought over 8888 and over the
        // named pipe "15881", and the second died writing the error to a console that
        // does not exist in a WinExe. A double click that did nothing and nobody knew why.

        private static Mutex? _candado;

        /// <summary>Takes this program's place. False if another already had it.</summary>
        public static bool CogerElSitio(string nombre)
        {
            try
            {
                _candado = new Mutex(initiallyOwned: true, @"Local\" + nombre, out bool nuestro);
                if (!nuestro)
                {
                    _candado.Dispose();
                    _candado = null;
                }
                return nuestro;
            }
            catch
            {
                // If the lock cannot be taken, better to let it start than prevent it: the ports'
                // failure warns afterwards, and now it warns well.
                return true;
            }
        }

        public static void SoltarElSitio()
        {
            try { _candado?.ReleaseMutex(); } catch { }
            _candado?.Dispose();
            _candado = null;
        }
    }
}
