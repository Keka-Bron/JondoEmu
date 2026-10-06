using System;
using System.IO;
using System.Security.Cryptography;
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
    /// That is why there is no logic here: route names, where the secret lives and the codes with which
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

        /// <summary>The header the secret travels in.</summary>
        public const string Cabecera = "X-Jondo-Control";

        // ─── The codes ──────────────────────────────────────────────────────────────────────
        //
        // The server says WHAT happened; the launcher decides HOW to tell the person and in which
        // language. If the server sent the ready-made sentence it would have to know the user's language, and
        // for that it would have to read a preferences file from someone's desktop.

        public const string MotivoSesionCaducada = "sesion-caducada";
        public const string MotivoCuentaYaAbierta = "cuenta-ya-abierta";
        public const string MotivoTopeDeClientes = "tope-de-clientes";

        // ─── The secret ─────────────────────────────────────────────────────────────────────
        //
        // The channel is on localhost, but on localhost is anything that runs on the
        // machine, and through here accounts are created and clients started. Whoever deleted these routes the
        // first time spoke of «una puerta abierta encima», and was right.
        //
        // The server invents a secret on each start and leaves it written in the user's
        // profile; the launcher reads it from there. Nobody types it and it does not leave the machine.
        //
        // CAREFUL: this is NOT checked today. The launcher sends the header and ControlApi never
        // reads it -- see ControlApi.Autorizada, which is written and has no callers -- so the two
        // paragraphs above describe an intention, not what happens. What actually closes the admin
        // routes is ConRol: a token the database recognises plus the administrator role. Spelled out
        // because taking this section at its word led to believing the channel was shut when what
        // shuts it is something else entirely.

        /// <summary>Where the secret lives: in the profile, next to the launcher's preferences.</summary>
        public static string FicheroDelSecreto => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Jondo", "control.secreto");

        /// <summary>Hands out a new secret and leaves it written. The server calls it on starting.</summary>
        public static string NuevoSecreto()
        {
            string secreto = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FicheroDelSecreto)!);
                File.WriteAllText(FicheroDelSecreto, secreto);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Control] No se ha podido escribir el secreto: {ex.Message}");
            }
            return secreto;
        }

        /// <summary>The secret the server left written, or an empty string if there is none.</summary>
        public static string LeerSecreto()
        {
            try
            {
                return File.Exists(FicheroDelSecreto) ? File.ReadAllText(FicheroDelSecreto).Trim() : "";
            }
            catch { return ""; }
        }

        /// <summary>Compares two secrets without the comparison's timing saying anything.</summary>
        public static bool MismoSecreto(string? uno, string? otro)
            => !string.IsNullOrEmpty(uno) && !string.IsNullOrEmpty(otro) &&
               CryptographicOperations.FixedTimeEquals(
                   System.Text.Encoding.UTF8.GetBytes(uno),
                   System.Text.Encoding.UTF8.GetBytes(otro));

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
