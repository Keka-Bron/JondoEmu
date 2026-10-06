using System;
using System.Collections.Generic;
using System.IO;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// What the launcher remembers from one time to the next.
    ///
    /// It lives in <c>%APPDATA%\Jondo\lanzador.cfg</c>, outside the emulator's folder on purpose:
    /// they are the user's preferences, not the emulator's data, and that way they neither dirty the directory nor
    /// go to the repository. The format is <c>key=value</c>, one per line, so it can be opened and
    /// fixed by hand if something goes wrong.
    ///
    ///   idioma=es|en|fr     the launcher's language, which is also the one the game starts with
    ///   cliente=C:\...\Dofus.exe   where the client is, if it is not where it is supposed to be
    ///   servidor=host-or-ip  the remote server; empty means this same machine
    ///   web=https://...      the website one logs in on; empty means there is none yet
    ///   cuentas=...          the stored accounts, ENCRYPTED (see SecretStore)
    ///   packHd=0|1           start the client with --hdReady when the HD pack is installed
    ///   pack4k=0|1           start the client with --4kReady when the 4K pack is installed
    /// </summary>
    internal static class LauncherPreferences
    {
        private const string ClaveIdioma = "idioma";
        private const string ClaveWeb = "web";
        private const string ClaveCliente = "cliente";
        private const string ClaveCuentas = "cuentas";

        internal sealed class SavedAccount
        {
            public long AccountId { get; set; }
            public string Login { get; set; } = "";
            public string Nickname { get; set; } = "";
            public string Token { get; set; } = "";
            public bool Selected { get; set; }

            /// <summary>The website's refresh token, when one logged in that way.</summary>
            /// <remarks>
            /// Empty while logging in with username and password, which is what is done today. It exists
            /// already so that the day there is a website the stored format does not have to change and
            /// throw out of the launcher everyone who had remembered accounts.
            /// </remarks>
            public string RefreshToken { get; set; } = "";

            /// <summary>When the access token expires, in Unix seconds. Zero if not known.</summary>
            public long ExpiresAtUnix { get; set; }
        }

        public static string Path { get; } = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jondo", "lanzador.cfg");

        private static Dictionary<string, string> Leer()
        {
            var valores = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(Path)) return valores;
                foreach (string linea in File.ReadAllLines(Path))
                {
                    int igual = linea.IndexOf('=');
                    if (igual <= 0) continue;
                    valores[linea.Substring(0, igual).Trim()] = linea.Substring(igual + 1).Trim();
                }
            }
            catch { }
            return valores;
        }

        private static void Escribir(string clave, string valor)
        {
            try
            {
                var valores = Leer();
                valores[clave] = valor;

                string? carpeta = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(carpeta) && !Directory.Exists(carpeta)) Directory.CreateDirectory(carpeta);

                var lineas = new List<string>();
                foreach (var par in valores) lineas.Add(par.Key + "=" + par.Value);
                File.WriteAllLines(Path, lineas);
            }
            catch { }
        }

        // ─── Idioma ─────────────────────────────────────────────────────────────

        public static Language Language
        {
            get => Leer().TryGetValue(ClaveIdioma, out string? v)
                ? v.Trim().ToLowerInvariant() switch
                {
                    "en" => UI.Language.En,
                    "fr" => UI.Language.Fr,
                    _ => UI.Language.Es,
                }
                : UI.Language.Es;
            set => Escribir(ClaveIdioma, LauncherTexts.Code(value));
        }

        // ─── Where the client is ────────────────────────────────────────────────

        /// <summary>
        /// The Dofus.exe chosen by hand, or an empty string if none has been chosen.
        ///
        /// It is checked that it still exists every time: if someone moves or deletes the client, what was
        /// stored stops being valid and it is looked for again in the default place, instead of failing
        /// with a path that no longer leads anywhere.
        /// </summary>
        public static string ClientExecutable
        {
            get
            {
                string ruta = Leer().TryGetValue(ClaveCliente, out string? v) ? v : "";
                return (!string.IsNullOrWhiteSpace(ruta) && File.Exists(ruta)) ? ruta : "";
            }
            set => Escribir(ClaveCliente, value ?? "");
        }

        /// <summary>What is stored as is, whether it exists or not. To be able to warn that it is no longer there.</summary>
        public static string ClientExecutableRaw
            => Leer().TryGetValue(ClaveCliente, out string? v) ? v : "";

        // ─── Where the server is ───────────────────────────────────────────────
        //
        // By default, this same machine: the case of playing locally, which is the usual one. The other
        // option is to write an address —that of a friend's computer over Hamachi, or that of a
        // VPS— and then the launcher starts no server: it connects to whatever is there.

        private const string ClaveServidor = "servidor";

        /// <summary>The server's address. Empty or "127.0.0.1" means right here.</summary>
        public static string ServerHost
        {
            get
            {
                string donde = Leer().TryGetValue(ClaveServidor, out string? v) ? v.Trim() : "";
                return donde.Length == 0 ? Contract.LocalIp : donde;
            }
            set => Escribir(ClaveServidor, (value ?? "").Trim());
        }

        /// <summary>
        /// The texts in the language the launcher has set.
        ///
        /// It lives here and not in LauncherTexts because the catalogue is shared by the launcher and the
        /// server, and each remembers its language on its own: they can be on different machines
        /// and belong to different people.
        /// </summary>
        public static LauncherTexts Textos => LauncherTexts.Get(Language);

        /// <summary>Whether the server is this machine's, which is what decides whether it can be started.</summary>
        public static bool ServerIsLocal
        {
            get
            {
                string donde = ServerHost;
                return donde == Contract.LocalIp || donde.Equals("localhost", StringComparison.OrdinalIgnoreCase);
            }
        }

        // ─── Texture packs ─────────────────────────────────────────────────────
        //
        // Whether the player wants each pack offered to the client. Wanting it is not enough: the
        // flag is only passed while the pack is installed and verified for the client's version,
        // see Packs.TexturePackService.LaunchFlags.

        private const string KeyPackHd = "packHd";
        private const string KeyPack4k = "pack4k";

        public static bool PackHd
        {
            get => Leer().TryGetValue(KeyPackHd, out string? v) && v.Trim() == "1";
            set => Escribir(KeyPackHd, value ? "1" : "0");
        }

        public static bool Pack4k
        {
            get => Leer().TryGetValue(KeyPack4k, out string? v) && v.Trim() == "1";
            set => Escribir(KeyPack4k, value ? "1" : "0");
        }

        // ─── Where one logs in ─────────────────────────────────────────────────

        /// <summary>The website where one signs in. Empty while it does not exist.</summary>
        /// <remarks>
        /// As soon as it has a value, the launcher stops asking for the password in its own window and
        /// opens the browser: see <see cref="Security.OAuthFlow"/>. It is a preference and not a
        /// constant to be able to point to a test website without recompiling.
        /// </remarks>
        public static string WebSite
        {
            get => Leer().TryGetValue(ClaveWeb, out string? v) ? v.Trim() : "";
            set => Escribir(ClaveWeb, (value ?? "").Trim());
        }

        /// <summary>Whether there is already a website to log in against.</summary>
        /// <remarks>
        /// https is required, except on loopback to be able to test against a local website. Sending
        /// people to type their password over http would be worse than the text box this is here
        /// to replace.
        /// </remarks>
        public static bool HasWebSite
        {
            get
            {
                string donde = WebSite;
                return donde.Length > 0
                       && Uri.TryCreate(donde, UriKind.Absolute, out var uri)
                       && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback);
            }
        }

        // ─── The multi-account team ────────────────────────────────────────────

        /// <summary>How many accounts are remembered at most.</summary>
        /// <remarks>
        /// Eight, which is the multi-account launcher's cap. It was written by hand in four places of
        /// this same file; with a constant one of the four cannot be left behind.
        /// </remarks>
        public const int MaxAccounts = 8;

        public static List<SavedAccount> LoadAccounts()
        {
            try
            {
                if (!Leer().TryGetValue(ClaveCuentas, out string? guardado) ||
                    string.IsNullOrWhiteSpace(guardado)) return new List<SavedAccount>();

                // The previous version's was plain Base64 —that is, nothing— and it is still read
                // once so as not to throw out of the launcher whoever already had it. As soon as it is saved,
                // it comes back encrypted.
                bool sinCifrar = Security.SecretStore.LooksUnprotected(guardado);
                string json = Security.SecretStore.Unprotect(guardado);
                if (json.Length == 0) return new List<SavedAccount>();

                var validas = (System.Text.Json.JsonSerializer.Deserialize<List<SavedAccount>>(json)
                               ?? new List<SavedAccount>())
                    .FindAll(a => a.AccountId > 0 && !string.IsNullOrWhiteSpace(a.Token));
                if (validas.Count > MaxAccounts) validas.RemoveRange(MaxAccounts, validas.Count - MaxAccounts);

                if (sinCifrar && validas.Count > 0)
                {
                    SaveAccounts(validas);
                    Console.WriteLine("[Lanzador] Las cuentas guardadas estaban sin cifrar; " +
                                      "se han vuelto a guardar cifradas.");
                }

                return validas;
            }
            catch { return new List<SavedAccount>(); }
        }

        public static void SaveAccounts(IEnumerable<SavedAccount> accounts)
        {
            var seguras = new List<SavedAccount>();
            foreach (var account in accounts)
            {
                if (seguras.Count == MaxAccounts) break;
                if (account.AccountId <= 0 || string.IsNullOrWhiteSpace(account.Token)) continue;
                seguras.Add(account);
            }

            string json = System.Text.Json.JsonSerializer.Serialize(seguras);

            // If it could not be encrypted, Protect returns empty and here whatever was there is erased. It is not
            // stored in the clear: it is better to ask for the session again than to leave credentials
            // readable in the player's profile.
            Escribir(ClaveCuentas, Security.SecretStore.Protect(json));
        }
    }
}
