using System;
using System.Collections.Generic;
using System.IO;

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
    ///   servidor=host-or-ip  el servidor remoto; vacío significa esta misma máquina
    ///   web=https://...      la web donde se entra; vacío significa que todavía no hay
    ///   cuentas=...          las cuentas guardadas, CIFRADAS (ver SecretStore)
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

            /// <summary>El vale de renovación de la web, cuando se entró por ahí.</summary>
            /// <remarks>
            /// Vacío mientras se entre con usuario y contraseña, que es lo que se hace hoy. Existe
            /// ya para que el día que haya web no haya que cambiar el formato de lo guardado y
            /// echar del lanzador a todo el que tuviera cuentas recordadas.
            /// </remarks>
            public string RefreshToken { get; set; } = "";

            /// <summary>Cuándo caduca el vale de acceso, en segundos Unix. Cero si no se sabe.</summary>
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

        /// <summary>La dirección del servidor. Vacío o "127.0.0.1" significa aquí mismo.</summary>
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

        // ─── Dónde se entra ────────────────────────────────────────────────────

        /// <summary>La web donde se inicia sesión. Vacío mientras no exista.</summary>
        /// <remarks>
        /// En cuanto tenga valor, el lanzador deja de pedir la contraseña en su propia ventana y
        /// abre el navegador: ver <see cref="Security.OAuthFlow"/>. Es una preferencia y no una
        /// constante para poder apuntar a una web de pruebas sin recompilar.
        /// </remarks>
        public static string WebSite
        {
            get => Leer().TryGetValue(ClaveWeb, out string? v) ? v.Trim() : "";
            set => Escribir(ClaveWeb, (value ?? "").Trim());
        }

        /// <summary>Si ya hay web contra la que entrar.</summary>
        /// <remarks>
        /// Se exige https, salvo en loopback para poder probar contra una web local. Mandar a la
        /// gente a escribir su contraseña por http sería peor que la caja de texto que esto viene
        /// a sustituir.
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

        // ─── El equipo multicuenta ─────────────────────────────────────────────

        /// <summary>Cuántas cuentas se recuerdan como mucho.</summary>
        /// <remarks>
        /// Ocho, que es el tope del lanzador multicuenta. Estaba escrito a mano en cuatro sitios de
        /// este mismo fichero; con una constante no puede quedarse uno de los cuatro atrás.
        /// </remarks>
        public const int MaxAccounts = 8;

        public static List<SavedAccount> LoadAccounts()
        {
            try
            {
                if (!Leer().TryGetValue(ClaveCuentas, out string? guardado) ||
                    string.IsNullOrWhiteSpace(guardado)) return new List<SavedAccount>();

                // Lo de la versión anterior era Base64 a secas —o sea, nada— y se sigue leyendo
                // una vez para no echar del lanzador a quien ya lo tenía. En cuanto se guarde,
                // vuelve cifrado.
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

            // Si no se ha podido cifrar, Protect devuelve vacío y aquí se borra lo que hubiera. No
            // se guarda en claro: vale más volver a pedir la sesión que dejar credenciales
            // legibles en el perfil de quien juega.
            Escribir(ClaveCuentas, Security.SecretStore.Protect(json));
        }
    }
}
