using Jondo.Unity.Launcher;
using Jondo.Unity.Launcher.UI;
using System;
using System.IO;

namespace Jondo.Unity.Server.UI
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

        public static Language Language
        {
            get
            {
                try
                {
                    if (!File.Exists(Fichero)) return Language.Es;
                    return File.ReadAllText(Fichero).Trim().ToLowerInvariant() switch
                    {
                        "en" => Language.En,
                        "fr" => Language.Fr,
                        _ => Language.Es,
                    };
                }
                catch { return Language.Es; }
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
