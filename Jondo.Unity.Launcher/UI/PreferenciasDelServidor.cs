using System;
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
    internal static class PreferenciasDelServidor
    {
        private static string Fichero => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jondo", "servidor.cfg");

        public static Language Language
        {
            get
            {
                try
                {
                    if (!File.Exists(Fichero)) return UI.Language.Es;
                    return File.ReadAllText(Fichero).Trim().ToLowerInvariant() switch
                    {
                        "en" => UI.Language.En,
                        "fr" => UI.Language.Fr,
                        _ => UI.Language.Es,
                    };
                }
                catch { return UI.Language.Es; }
            }
            set
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Fichero)!);
                    File.WriteAllText(Fichero, LauncherTexts.Code(value));
                }
                catch { }
            }
        }
    }
}
