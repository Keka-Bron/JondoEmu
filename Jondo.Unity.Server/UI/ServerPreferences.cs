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
        /// <summary>
        /// The window's language, kept in the server's settings file inside the emulator's folder
        /// (config\server_settings.json). Changing it takes effect at once and needs no restart.
        /// </summary>
        public static Language Language
        {
            get => ServerSettings.Load().WindowLanguage?.Trim().ToLowerInvariant() switch
            {
                "en" => Language.En,
                "fr" => Language.Fr,
                _ => Language.Es,
            };
            set
            {
                try
                {
                    var saved = ServerSettings.Load();
                    saved.WindowLanguage = LauncherTexts.Code(value);
                    saved.Save();
                }
                catch { }
            }
        }
    }
}
