using Jondo.Unity.Launcher;
using Jondo.Unity.Launcher.UI;
using System;
using System.IO;

namespace Jondo.Unity.Server.UI
{
    /// <summary>
    /// What the server window remembers from one start to the next.
    ///
    /// Separate from the launcher's on purpose: they can be on different machines and belong to different
    /// people, and the language in which whoever runs the server wants to see his console need not
    /// be the same one anybody plays in.
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
