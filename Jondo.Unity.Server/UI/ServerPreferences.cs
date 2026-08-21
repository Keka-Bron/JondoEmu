using System;
using System.Collections.Generic;
using System.IO;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// Lo que la ventana del servidor recuerda entre un arranque y el siguiente.
    ///
    /// Aparte de las del lanzador a propósito: pueden estar en máquinas distintas y de personas
    /// distintas, y el idioma en el que quiere ver su consola quien lleva el servidor no tiene por
    /// qué ser el mismo en el que juega nadie.
    /// </summary>
    internal static class ServerPreferences
    {
        private static string Fichero => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jondo", "servidor.cfg");

        // ─── Leer y escribir ──────────────────────────────────────────────────────────────────
        //
        // Antes el fichero entero ERA el idioma: una linea, «es». Al guardar algo más que el idioma
        // hace falta clave=valor, y las instalaciones que ya tienen su fichero viejo con una sola
        // palabra no pueden leerse como si fueran pares: lo que no lleva «=» se toma por el idioma
        // de siempre, y a la primera escritura ya queda convertido.

        private static Dictionary<string, string> Leer()
        {
            var valores = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(Fichero)) return valores;
                foreach (string linea in File.ReadAllLines(Fichero))
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

                // El idioma se guardaba pelado, sin clave. Si el fichero viejo no era clave=valor,
                // su contenido entero era el idioma: se rescata antes de pisarlo.
                if (!valores.ContainsKey("idioma"))
                {
                    try
                    {
                        if (File.Exists(Fichero))
                        {
                            string pelado = File.ReadAllText(Fichero).Trim();
                            if (pelado.Length > 0 && !pelado.Contains('=')) valores["idioma"] = pelado;
                        }
                    }
                    catch { }
                }

                valores[clave] = valor;
                Directory.CreateDirectory(Path.GetDirectoryName(Fichero)!);
                var lineas = new List<string>();
                foreach (var par in valores) lineas.Add(par.Key + "=" + par.Value);
                File.WriteAllLines(Fichero, lineas);
            }
            catch { }
        }

        public static Language Language
        {
            get
            {
                try
                {
                    var valores = Leer();
                    string idioma = valores.TryGetValue("idioma", out string? conClave)
                        ? conClave
                        : (File.Exists(Fichero) ? File.ReadAllText(Fichero).Trim() : "es");
                    return idioma.ToLowerInvariant() switch
                    {
                        "en" => UI.Language.En,
                        "fr" => UI.Language.Fr,
                        _ => UI.Language.Es,
                    };
                }
                catch { return UI.Language.Es; }
            }
            set => Escribir("idioma", LauncherTexts.Code(value));
        }

        // ─── El tamaño de la interfaz ─────────────────────────────────────────────────────────

        /// <summary>La ampliación de la ventana del servidor. 1 = sin ampliar.</summary>
        public static float Zoom
        {
            get
            {
                var valores = Leer();
                if (!valores.TryGetValue("zoom", out string? v)) return 1f;
                return float.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out float zoom)
                    ? MathF.Max(0.75f, MathF.Min(2f, zoom))
                    : 1f;
            }
            set
            {
                float zoom = MathF.Max(0.75f, MathF.Min(2f, value));
                Escribir("zoom", zoom.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }
}
