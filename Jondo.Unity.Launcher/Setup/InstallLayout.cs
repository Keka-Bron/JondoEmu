using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jondo.Unity.Launcher.Setup
{
    /// <summary>
    /// Where the emulator is installed and what the installation holds, as <c>install.json</c>
    /// in its folder says: the version, the repository its updates come from, every file the
    /// release put there with its hash, the client and its MelonLoader.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The folder is the one the installer chose: <c>C:\Program Files\JondoEmu</c> on Windows,
    /// <c>~/.local/share/JondoEmu</c> on Linux, unless the player picked another. Everything the
    /// release brings lives there beside the launcher; what the emulator writes for itself --
    /// <c>bases\</c>, <c>config\</c>, <c>logs\</c> -- too, and no update touches it: only the
    /// files the manifest names are replaced.
    /// </para>
    /// <para>
    /// An executable with no <c>install.json</c> beside it is not installed: it is the
    /// downloaded setup, and the window offers to install.
    /// </para>
    /// </remarks>
    internal sealed class InstallLayout
    {
        public const string StateFileName = "install.json";
        public const string RootMarker = ".jondo-root";
        public const string FolderName = "JondoEmu";

        public InstallLayout(string root) => Root = Path.GetFullPath(root);

        public string Root { get; }
        public string StateFile => Path.Combine(Root, StateFileName);
        public string LauncherExecutable => Path.Combine(Root, LauncherFileName);
        public string ServerExecutable => Path.Combine(Root, Program.EjecutableDelServidor);
        public string DownloadsDirectory => Path.Combine(Root, "downloads");
        public string DefaultClientDirectory => Path.Combine(Root, "Cliente " + ClientDownload.Version);

        public static string LauncherFileName => OperatingSystem.IsWindows() ? "Jondo Emulator Launcher.exe" : "Jondo Emulator Launcher";

        /// <summary>The folder offered on a first install.</summary>
        public static string DefaultRoot
        {
            get
            {
                if (OperatingSystem.IsWindows())
                {
                    string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                    if (string.IsNullOrEmpty(programs)) programs = @"C:\Program Files";
                    return Path.Combine(programs, FolderName);
                }
                return Path.Combine(UserDirectories.Data, FolderName);
            }
        }

        /// <summary>The installation the running launcher belongs to, or null when it is the setup.</summary>
        public static InstallLayout? Current
        {
            get
            {
                string? here = Path.GetDirectoryName(Environment.ProcessPath ?? "");
                if (string.IsNullOrEmpty(here)) return null;
                var layout = new InstallLayout(here);
                return File.Exists(layout.StateFile) ? layout : null;
            }
        }

        /// <summary>
        /// On Linux, the executables runnable after they were unpacked: an archive's Unix
        /// permissions do not survive every unpacker.
        /// </summary>
        public void MarkExecutables()
        {
            if (OperatingSystem.IsWindows()) return;
            foreach (string name in new[] { LauncherFileName, Program.EjecutableDelServidor, "Jondo Studio" })
            {
                string path = Path.Combine(Root, name);
                if (File.Exists(path))
                    File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
            }
        }

        public InstallState Load() => InstallState.Read(StateFile);

        public void Save(InstallState state) => state.Write(StateFile);

        /// <summary>
        /// What an update leaves behind: the files that were in use when they were replaced, put
        /// aside as <c>.old</c>, and a replacement that never got into place. Only beside the files
        /// the release installed, so a start does not walk the client's thousands of files.
        /// </summary>
        public static void RemoveLeftovers(string root)
        {
            try
            {
                var state = InstallState.Read(Path.Combine(root, StateFileName));
                var candidates = state.Files.Keys.Select(p => Path.Combine(root, p))
                    .Concat(Directory.EnumerateFiles(root).Select(f => Path.ChangeExtension(f, null)));
                foreach (string file in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                    foreach (string leftover in new[] { file + ".old", file + ".jondo-new" })
                        if (File.Exists(leftover))
                            try { File.Delete(leftover); }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[Launcher] Leftovers of the last update could not be removed: {ex.Message}");
            }
        }
    }

    /// <summary>What <c>install.json</c> holds.</summary>
    internal sealed class InstallState
    {
        /// <summary>The release installed, as its tag says it: «v0.2.30». Empty while the first install is under way.</summary>
        public string Version { get; set; } = "";

        /// <summary>The GitHub repository updates are looked for in: «owner/name».</summary>
        public string Repository { get; set; } = "";

        /// <summary>The platform of the release's files: «win-x64», «linux-x64».</summary>
        public string Platform { get; set; } = "";

        /// <summary>Every file the release put in the folder, by its path there, with its hash.</summary>
        public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The client's executable, once known.</summary>
        public string Client { get; set; } = "";

        /// <summary>Whether the launcher downloaded the client itself (into the folder) or was shown one.</summary>
        public bool ClientDownloaded { get; set; }

        /// <summary>The MelonLoader version put in the client, empty while none.</summary>
        public string MelonLoader { get; set; } = "";

        /// <summary>Whether MelonLoader has run once on this client and generated its assemblies.</summary>
        public bool Generated { get; set; }

        /// <summary>When it was installed and last updated, for the settings page.</summary>
        public DateTime InstalledAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        /// <summary>Whether everything a first install does has been done.</summary>
        [JsonIgnore]
        public bool Complete => Version.Length > 0 && Client.Length > 0
                                && (!OperatingSystem.IsWindows() || (MelonLoader.Length > 0 && Generated));

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public static InstallState Read(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var state = JsonSerializer.Deserialize<InstallState>(File.ReadAllText(path), Options);
                    if (state != null)
                    {
                        state.Files = new Dictionary<string, string>(state.Files ?? new(), StringComparer.OrdinalIgnoreCase);
                        return state;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Console.WriteLine($"[Setup] {path} could not be read ({ex.Message}); starting from nothing.");
            }
            return new InstallState();
        }

        /// <summary>Written beside and moved over, so a crash halfway never leaves half a file.</summary>
        public void Write(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));
            File.Move(temporary, path, overwrite: true);
        }

        /// <summary>The installed files sorted, for logs.</summary>
        public override string ToString()
            => $"{Version} from {Repository} ({Platform}), {Files.Count} files, client {Client}, " +
               $"MelonLoader {(MelonLoader.Length > 0 ? MelonLoader : "none")}{(Generated ? " generated" : "")}";
    }
}
