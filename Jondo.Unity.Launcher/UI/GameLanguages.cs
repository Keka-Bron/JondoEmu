using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// The languages the game itself can start in, which are more than the launcher's three.
    /// </summary>
    /// <remarks>
    /// The client carries every language inside it: one text pack per language in
    /// <c>Dofus_Data/StreamingAssets/Content/I18n/{code}.bin</c>, and the <c>--langCode</c> it is
    /// started with picks which one it reads. Version 3.6 brings five: es, en, fr, de, pt. They are
    /// read off the disk rather than written down here, so a pack a later patch adds shows up in
    /// the dropdown without touching this, with its code for a name and a blank flag.
    ///
    /// The launcher's own texts stay in three languages. That a German player can start the game
    /// in German does not oblige anyone to translate the launcher into German.
    /// </remarks>
    internal static class GameLanguages
    {
        /// <summary>Each language under its own name, and in the order the dropdown lists them.</summary>
        private static readonly (string Code, string Name)[] Known =
        {
            ("es", "Español"),
            ("en", "English"),
            ("fr", "Français"),
            ("de", "Deutsch"),
            ("pt", "Português"),
        };

        /// <summary>The name a language has in itself; the code in capitals if it is not known.</summary>
        public static string NameOf(string code)
        {
            foreach (var (conocido, nombre) in Known)
                if (conocido == code) return nombre;
            return code.ToUpperInvariant();
        }

        /// <summary>The client's folder of text packs, next to its Dofus.exe.</summary>
        public static string PacksFolder(string clientExecutable)
            => Path.Combine(Path.GetDirectoryName(clientExecutable) ?? "",
                            "Dofus_Data", "StreamingAssets", "Content", "I18n");

        /// <summary>
        /// The codes of the packs the client has, the known ones first in their order.
        /// </summary>
        /// <remarks>
        /// With no client found, or a folder that cannot be read, it answers the five of 3.6: the
        /// dropdown has to show something, and launching is not possible then anyway.
        /// </remarks>
        public static IReadOnlyList<string> Available(string clientExecutable)
        {
            var enDisco = new List<string>();
            try
            {
                if (clientExecutable.Length > 0)
                {
                    string carpeta = PacksFolder(clientExecutable);
                    if (Directory.Exists(carpeta))
                    {
                        enDisco.AddRange(Directory.EnumerateFiles(carpeta, "*.bin")
                            .Select(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant())
                            .Where(EsCodigo));
                    }
                }
            }
            catch
            {
                enDisco.Clear();
            }

            if (enDisco.Count == 0) return Known.Select(k => k.Code).ToArray();

            return Known.Select(k => k.Code).Where(enDisco.Contains)
                .Concat(enDisco.Except(Known.Select(k => k.Code)).OrderBy(c => c, StringComparer.Ordinal))
                .Distinct()
                .ToArray();
        }

        /// <summary>
        /// The <c>--langCode</c> to start the client with.
        /// </summary>
        /// <param name="chosen">What the dropdown remembers; empty means "the launcher's".</param>
        /// <remarks>
        /// A choice the client no longer has —another client chosen, a patch that dropped a pack—
        /// goes back to the launcher's language instead of starting a game that finds no texts.
        /// </remarks>
        public static string ForLaunch(Language launcher, string chosen, string clientExecutable)
        {
            if (chosen.Length > 0 && Available(clientExecutable).Contains(chosen)) return chosen;
            return LauncherTexts.Code(launcher);
        }

        /// <summary>Two or three lowercase letters: what a pack's name is, and nothing that can go on a command line.</summary>
        internal static bool EsCodigo(string code)
            => code.Length is >= 2 and <= 3 && code.All(c => c is >= 'a' and <= 'z');
    }
}
