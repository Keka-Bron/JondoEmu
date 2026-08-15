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
    }
}
