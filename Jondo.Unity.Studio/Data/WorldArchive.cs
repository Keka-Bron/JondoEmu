using System;
using System.IO;
using System.IO.Compression;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Studio.Data
{
    /// <summary>
    /// Unpacks <c>world.db</c> from the archive that travels with the repository.
    /// </summary>
    /// <remarks>
    /// The database is 242 MB, so what git carries is <c>datos/world.zip</c> at 24 MB and the
    /// emulator unpacks it the first time it runs. The server has always done that; the editor
    /// never did, so somebody who cloned the repository and opened the Studio first got every
    /// screen empty and no explanation — the maps, the NPCs, the monsters and the spells all live
    /// in that file.
    ///
    /// This is deliberately less clever than the server's copy, which also re-extracts when the
    /// database is present but missing tables. The editor only asks the simpler question — is it
    /// there at all — because it never writes to <c>world.db</c> and has no business deciding that
    /// somebody else's database is stale.
    /// </remarks>
    public static class WorldArchive
    {
        /// <summary>What happened, for the overview screen. Empty when there was nothing to do.</summary>
        public static string Report { get; private set; } = "";

        /// <summary>
        /// Unpacks the database if it is not there. Returns true when it is available afterwards.
        /// </summary>
        public static bool Ensure(Action<string>? report = null)
        {
            string database = Paths.WorldDb;
            if (File.Exists(database)) return true;

            string archive = Paths.WorldZip;
            if (!File.Exists(archive))
            {
                Report = $"{Path.GetFileName(database)} is not there and neither is " +
                         $"{Path.GetFileName(archive)} to unpack it from.";
                report?.Invoke(Report);
                return false;
            }

            try
            {
                string? into = Path.GetDirectoryName(database);
                if (string.IsNullOrEmpty(into))
                {
                    Report = "there is nowhere to unpack the database to.";
                    report?.Invoke(Report);
                    return false;
                }

                Directory.CreateDirectory(into);
                ZipFile.ExtractToDirectory(archive, into, overwriteFiles: true);

                bool there = File.Exists(database);
                Report = there
                    ? $"unpacked {Path.GetFileName(database)} from {Path.GetFileName(archive)}."
                    : $"{Path.GetFileName(archive)} unpacked but no {Path.GetFileName(database)} came out.";

                report?.Invoke(Report);
                return there;
            }
            catch (Exception ex)
            {
                Report = $"{Path.GetFileName(archive)} could not be unpacked: {ex.Message}";
                report?.Invoke(Report);
                return false;
            }
        }
    }
}
