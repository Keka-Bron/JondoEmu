using System;
using System.Collections.Generic;
using System.IO;

namespace Jondo.Unity.Admin
{
    /// <summary>
    /// Lo que el panel recuerda entre una vez y la siguiente: su ampliación.
    ///
    /// Vive en <c>%APPDATA%\Jondo\admin.cfg</c>, junto a las preferencias del lanzador y las de la
    /// ventana del servidor. La PRIMERA vez que se abre, si aún no tiene ampliación propia, hereda
    /// la que ya se haya elegido en cualquiera de los otros dos: es la misma máquina y la misma
    /// pantalla, y quien agrandó el lanzador quiere el panel agrandado también.
    /// </summary>
    internal static class AdminPreferences
    {
        private const string ClaveZoom = "zoom";

        private static string Fichero => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jondo", "admin.cfg");

        private static string Carpeta => Path.GetDirectoryName(Fichero)!;

        public static float Zoom
        {
            get
            {
                var propios = Leer(Fichero);
                if (propios.TryGetValue(ClaveZoom, out string? v) &&
                    float.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out float zoom))
                    return Math.Clamp(zoom, 0.5f, 3f);

                foreach (string vecino in new[] { "lanzador.cfg", "servidor.cfg" })
                {
                    var suyos = Leer(Path.Combine(Carpeta, vecino));
                    if (suyos.TryGetValue(ClaveZoom, out string? suyo) &&
                        float.TryParse(suyo, System.Globalization.CultureInfo.InvariantCulture, out float suZoom))
                        return Math.Clamp(suZoom, 0.5f, 3f);
                }
                return 1f;
            }
            set => Escribir(ClaveZoom, value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        }

        private static Dictionary<string, string> Leer(string fichero)
        {
            var valores = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(fichero)) return valores;
                foreach (string linea in File.ReadAllLines(fichero))
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
                var valores = Leer(Fichero);
                valores[clave] = valor;
                Directory.CreateDirectory(Carpeta);
                var lineas = new List<string>();
                foreach (var par in valores) lineas.Add(par.Key + "=" + par.Value);
                File.WriteAllLines(Fichero, lineas);
            }
            catch { }
        }
    }
}
