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
    /// </summary>
    internal static class LauncherPreferences
    {
        private const string ClaveIdioma = "idioma";
        private const string ClaveCliente = "cliente";
        private const string ClaveCuentas = "cuentas";

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

        // ─── Équipe multicomptes ───────────────────────────────────────────────

        public static List<SavedAccount> LoadAccounts()
        {
            try
            {
                if (!Leer().TryGetValue(ClaveCuentas, out string? encoded) ||
                    string.IsNullOrWhiteSpace(encoded)) return new List<SavedAccount>();
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
            Escribir(ClaveCuentas, Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)));
        }
    }
}
