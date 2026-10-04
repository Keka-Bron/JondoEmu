using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Jondo.Unity.Launcher.Packs
{
    /// <summary>
    /// The version the client folder says it is.
    /// </summary>
    /// <remarks>
    /// Read from <c>Dofus_Data\StreamingAssets\version</c>, a <c>key=value</c> file the client
    /// ships with (<c>Version=3.6.10.11</c>, then the build date and a config URL).
    ///
    /// It is deliberately the ONLY source. <see cref="LauncherService.Version"/> names the
    /// protocol the emulator speaks, and the client beside it may be a later patch of that same
    /// protocol: today it is 3.6.10.11 while the constant says 3.6.10.10. The texture packs are
    /// downloaded for the files actually installed, so there is no fallback to any constant or
    /// to another version: no readable version file means no download.
    /// </remarks>
    internal static class ClientVersion
    {
        /// <summary>Where the version file is, relative to the client folder.</summary>
        public static string RelativePath { get; } = Path.Combine("Dofus_Data", "StreamingAssets", "version");

        /// <summary>
        /// Digits and dots only. The value ends up in a URL and in file names, so anything else
        /// counts as unreadable rather than being passed along.
        /// </summary>
        private static readonly Regex Shape = new(@"^\d{1,6}(\.\d{1,6}){1,5}$", RegexOptions.CultureInvariant);

        /// <summary>The client's version, such as <c>3.6.10.11</c>, or null if it cannot be read.</summary>
        public static string? Read(string clientRoot)
        {
            if (string.IsNullOrEmpty(clientRoot)) return null;

            try
            {
                string path = Path.Combine(clientRoot, RelativePath);
                if (!File.Exists(path)) return null;

                foreach (string line in File.ReadLines(path))
                {
                    int equals = line.IndexOf('=');
                    if (equals <= 0) continue;
                    if (!line.Substring(0, equals).Trim().Equals("Version", StringComparison.OrdinalIgnoreCase)) continue;

                    string value = line.Substring(equals + 1).Trim();
                    return Shape.IsMatch(value) ? value : null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable is the same as missing: no version, no download.
            }

            return null;
        }
    }
}
