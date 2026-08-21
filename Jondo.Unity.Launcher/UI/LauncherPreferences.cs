using System;
using System.Collections.Generic;
using System.IO;
using System.Net;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// Lo que el lanzador recuerda entre una vez y la siguiente.
    ///
    /// Vive en <c>%APPDATA%\Jondo\lanzador.cfg</c>, fuera de la carpeta del emulador a propósito:
    /// son preferencias de quien lo usa, no datos del emulador, y así ni ensucian el directorio ni
    /// se van al repositorio. El formato es <c>clave=valor</c>, una por línea, para poder abrirlo y
    /// arreglarlo a mano si algo se tuerce.
    ///
    ///   idioma=es|en|fr     el idioma del lanzador, que es también con el que arranca el juego
    ///   cliente=C:\...\Dofus.exe   dónde está el cliente, si no está donde se supone
    /// </summary>
    internal static class LauncherPreferences
    {
        private const string ClaveIdioma = "idioma";
        private const string ClaveCliente = "cliente";
        private const string ClaveCuentas = "cuentas";
        private const string ClaveZoom = "zoom";

        internal sealed class SavedAccount
        {
            public long AccountId { get; set; }
            public string Login { get; set; } = "";
            public string Nickname { get; set; } = "";
            public string Token { get; set; } = "";
            public bool Selected { get; set; }
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

        // ─── El tamaño de la interfaz ─────────────────────────────────────────────────────────
        //
        // Para pantallas de muchas pulgadas con la escala de Windows al 100%, donde 96 dpi hace
        // que todo quede diminuto. 1 es el tamaño de siempre.

        /// <summary>La ampliación de la interfaz del lanzador. 1 = sin ampliar.</summary>
        public static float Zoom
        {
            get
            {
                var valores = Leer();
                if (!valores.TryGetValue(ClaveZoom, out string? v)) return 1f;
                return float.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out float zoom)
                    ? MathF.Max(0.5f, MathF.Min(3f, zoom))
                    : 1f;
            }
            set => Escribir(ClaveZoom, value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        }

        // ─── Dónde está el cliente ──────────────────────────────────────────────

        /// <summary>
        /// El Dofus.exe elegido a mano, o cadena vacía si no se ha elegido ninguno.
        ///
        /// Se comprueba que siga existiendo cada vez: si alguien mueve o borra el cliente, lo
        /// guardado deja de valer y se vuelve a buscar donde se busca por defecto, en vez de fallar
        /// con una ruta que ya no lleva a ninguna parte.
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

        /// <summary>Lo guardado tal cual, exista o no. Para poder avisar de que ya no está.</summary>
        public static string ClientExecutableRaw
            => Leer().TryGetValue(ClaveCliente, out string? v) ? v : "";

        // ─── Dónde está el servidor ────────────────────────────────────────────
        //
        // Por defecto, esta misma máquina: el caso de jugar en local, que es el de siempre. La otra
        // opción es escribir una dirección —la del ordenador de un amigo por Hamachi, o la de una
        // VPS— y entonces el lanzador no arranca ningún servidor: se conecta al que haya allí.

        private const string ClaveServidor = "servidor";
        private const string ClaveControl = "control";

        /// <summary>La dirección del servidor. Vacío o "127.0.0.1" significa aquí mismo.</summary>
        public static string ServerHost
        {
            get
            {
                string donde = Leer().TryGetValue(ClaveServidor, out string? v) ? v.Trim() : "";
                return TryNormalizeServerHost(donde, out string normalizado)
                    ? normalizado
                    : Contract.LocalIp;
            }
            set
            {
                if (!TryNormalizeServerEndpoint(value, out string normalizado, out string control))
                    throw new ArgumentException("The server address must be a host name or IP address.", nameof(value));
                Escribir(ClaveServidor, normalizado);
                Escribir(ClaveControl, control);
            }
        }

        /// <summary>
        /// HTTPS (or local HTTP) endpoint used only by the launcher's account/control API. Game,
        /// chat and Zaap still use <see cref="ServerHost"/> on their native fixed ports.
        /// </summary>
        public static string ControlBaseUrl
        {
            get
            {
                var values = Leer();
                if (values.TryGetValue(ClaveControl, out string? saved) &&
                    TryNormalizeServerEndpoint(saved, out string savedHost, out string endpoint) &&
                    savedHost.Equals(ServerHost, StringComparison.OrdinalIgnoreCase))
                {
                    return endpoint;
                }

                return DefaultControlEndpoint(ServerHost);
            }
        }

        /// <summary>The value shown in the editor: concise locally, explicit for TLS/proxy URLs.</summary>
        public static string ServerEndpointDisplay
        {
            get
            {
                string endpoint = ControlBaseUrl;
                return endpoint.Equals(DefaultControlEndpoint(ServerHost), StringComparison.OrdinalIgnoreCase)
                    ? ServerHost
                    : endpoint;
            }
        }

        /// <summary>Saves the native-service host and launcher-control URL as one atomic choice.</summary>
        public static void SetServerEndpoint(string value)
        {
            if (!TryNormalizeServerEndpoint(value, out string host, out string control))
                throw new ArgumentException("The server endpoint is invalid.", nameof(value));

            Escribir(ClaveServidor, host);
            Escribir(ClaveControl, control);
        }

        /// <summary>
        /// Normalises the address shared by the launcher's control client and JondoFix. Ports are
        /// deliberately not accepted here: the Dofus services use several fixed ports, so a
        /// single host name is the only value that can consistently describe all of them.
        /// </summary>
        public static bool TryNormalizeServerHost(string? value, out string host)
            => TryNormalizeServerEndpoint(value, out host, out _);

        /// <summary>
        /// Accepts either a native-service host or a complete HTTP(S) control URL. A complete URL
        /// may carry a reverse-proxy port, but must not contain credentials, a path, query or
        /// fragment. Its host remains the destination for Dofus' fixed protocol ports.
        /// </summary>
        public static bool TryNormalizeServerEndpoint(string? value, out string host,
                                                       out string controlBaseUrl)
        {
            host = (value ?? "").Trim();
            controlBaseUrl = "";
            if (host.Length == 0)
            {
                host = Contract.LocalIp;
                controlBaseUrl = DefaultControlEndpoint(host);
                return true;
            }

            // A URL describes the TLS-facing control endpoint. Its host is also used for the
            // native game protocols, whose ports are fixed and therefore deliberately omitted.
            if (host.Contains("://", StringComparison.Ordinal))
            {
                if (!Uri.TryCreate(host, UriKind.Absolute, out Uri? uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                    !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" ||
                    !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
                    !TryNormalizeHostOnly(uri.Host, out string urlHost))
                {
                    host = "";
                    return false;
                }

                host = urlHost;
                var endpoint = new UriBuilder(uri.Scheme, host, uri.IsDefaultPort ? -1 : uri.Port);
                controlBaseUrl = endpoint.Uri.GetLeftPart(UriPartial.Authority);
                return true;
            }

            if (!TryNormalizeHostOnly(host, out host)) return false;
            controlBaseUrl = DefaultControlEndpoint(host);
            return true;
        }

        private static bool TryNormalizeHostOnly(string value, out string host)
        {
            host = value.Trim();
            if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
            {
                host = host[1..^1];
            }

            if (IPAddress.TryParse(host, out IPAddress? address))
            {
                host = address.ToString();
                return true;
            }

            if (host.Contains('/') || host.Contains('\\') || host.Any(char.IsWhiteSpace) ||
                Uri.CheckHostName(host) != UriHostNameType.Dns)
            {
                host = "";
                return false;
            }

            host = host.TrimEnd('.').ToLowerInvariant();
            return host.Length > 0;
        }

        private static string DefaultControlEndpoint(string host)
        {
            var endpoint = new UriBuilder(Uri.UriSchemeHttp, host, Contract.Puerto);
            return endpoint.Uri.GetLeftPart(UriPartial.Authority);
        }

        /// <summary>
        /// Los textos en el idioma que tenga puesto el lanzador.
        ///
        /// Vive aquí y no en LauncherTexts porque el catálogo lo comparten el lanzador y el
        /// servidor, y cada uno recuerda su idioma por su cuenta: pueden estar en máquinas
        /// distintas y de personas distintas.
        /// </summary>
        public static LauncherTexts Textos => LauncherTexts.Get(Language);

        /// <summary>Si el servidor es el de esta máquina, que es lo que decide si se puede arrancar.</summary>
        public static bool ServerIsLocal
        {
            get
            {
                string donde = ServerHost;
                return donde == Contract.LocalIp || donde.Equals("localhost", StringComparison.OrdinalIgnoreCase);
            }
        }

        // ─── Équipe multicomptes ───────────────────────────────────────────────

        public static List<SavedAccount> LoadAccounts()
        {
            try
            {
                var values = Leer();
                if (!values.TryGetValue(ScopedAccountsKey, out string? encoded) && ServerIsLocal)
                {
                    // One-time compatibility with profiles saved before teams became scoped to
                    // a server. The next save writes them under the local endpoint key.
                    values.TryGetValue(ClaveCuentas, out encoded);
                }
                if (string.IsNullOrWhiteSpace(encoded)) return new List<SavedAccount>();
                string json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                var valid = (System.Text.Json.JsonSerializer.Deserialize<List<SavedAccount>>(json)
                             ?? new List<SavedAccount>())
                    .FindAll(a => a.AccountId > 0 && !string.IsNullOrWhiteSpace(a.Token));
                if (valid.Count > 8) valid.RemoveRange(8, valid.Count - 8);
                return valid;
            }
            catch { return new List<SavedAccount>(); }
        }

        public static void SaveAccounts(IEnumerable<SavedAccount> accounts)
        {
            var safe = new List<SavedAccount>();
            foreach (var account in accounts)
            {
                if (safe.Count == 8) break;
                if (account.AccountId <= 0 || string.IsNullOrWhiteSpace(account.Token)) continue;
                safe.Add(account);
            }
            string json = System.Text.Json.JsonSerializer.Serialize(safe);
            Escribir(ScopedAccountsKey, Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)));
        }

        private static string ScopedAccountsKey
        {
            get
            {
                byte[] host = System.Text.Encoding.UTF8.GetBytes(ServerHost.ToLowerInvariant());
                string suffix = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(host))[..16];
                return ClaveCuentas + "." + suffix;
            }
        }
    }
}
