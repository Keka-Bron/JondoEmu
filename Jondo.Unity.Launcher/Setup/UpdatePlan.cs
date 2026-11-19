using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jondo.Unity.Launcher.Setup
{
    /// <summary>
    /// The list of files a release puts in the installation folder, published beside it as
    /// <c>manifest-&lt;platform&gt;.json</c> by the Release workflow.
    /// </summary>
    /// <remarks>
    /// Each file says where a single copy of it can be had: the repository's own files (datos,
    /// content, the mods) straight from GitHub at the release's tag (<see cref="ManifestFile.Raw"/>);
    /// the executables, which are built and not in the repository, as assets of their own. The
    /// whole folder is also one zip (<see cref="Full"/>), for a first install or an update that
    /// changes most of it.
    /// </remarks>
    internal sealed class ReleaseManifest
    {
        public string Version { get; set; } = "";
        public string Platform { get; set; } = "";

        /// <summary>The zip with the whole folder, «JondoEmu-v1.0.12-win-x64.zip»; its root folder is JondoEmu/.</summary>
        public string Full { get; set; } = "";

        /// <summary>The setup's own asset, the launcher under the name players download.</summary>
        public string Setup { get; set; } = "";

        public List<ManifestFile> Files { get; set; } = new();

        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        };

        public static ReleaseManifest Parse(string json)
        {
            var manifest = JsonSerializer.Deserialize<ReleaseManifest>(json, Options)
                           ?? throw new SetupException("The release's manifest is empty.");
            foreach (var file in manifest.Files)
            {
                if (!IsSafeRelativePath(file.Path))
                    throw new SetupException($"The release's manifest names a file outside the folder: «{file.Path}».");
                if (file.Sha256.Length != 64)
                    throw new SetupException($"The release's manifest has no hash for «{file.Path}».");
            }
            return manifest;
        }

        public string ToJson() => JsonSerializer.Serialize(this, Options);

        /// <summary>
        /// A path inside the folder: relative, no «..», no drive, no root. A manifest that named
        /// «..\..\Windows\x» would otherwise write wherever it liked.
        /// </summary>
        internal static bool IsSafeRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string p = path.Replace('\\', '/');
            if (p.StartsWith('/') || p.Contains(':')) return false;
            return p.Split('/').All(part => part.Length > 0 && part != "." && part != "..");
        }
    }

    /// <summary>One file of a release.</summary>
    internal sealed class ManifestFile
    {
        /// <summary>Its place in the folder, with forward slashes: «datos/world.zip».</summary>
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public string Sha256 { get; set; } = "";

        /// <summary>The release asset that is this very file, for the executables.</summary>
        public string Asset { get; set; } = "";

        /// <summary>Whether it is the repository's own file, fetched from GitHub at the release's tag.</summary>
        public bool Raw { get; set; }
    }

    /// <summary>
    /// What an update has to do: the files to fetch, the ones the new release no longer has, and
    /// whether fetching them one by one is worth it or the whole zip is.
    /// </summary>
    internal sealed class UpdatePlan
    {
        /// <summary>Past this share of the full zip's bytes, or this many files, the whole zip goes instead.</summary>
        public const double FullShare = 0.6;
        public const int MaxSingleFiles = 300;

        public List<ManifestFile> Fetch { get; } = new();
        public List<string> Remove { get; } = new();

        /// <summary>Whether the whole folder comes in the full zip.</summary>
        public bool Full { get; private set; }

        public long BytesToFetch => Fetch.Sum(f => f.Size);

        /// <summary>Whether the launcher itself is among what changes: it is replaced last, and starts again.</summary>
        public bool ReplacesLauncher => Fetch.Any(f => IsLauncher(f.Path));

        public bool Nothing => Fetch.Count == 0 && Remove.Count == 0;

        public static bool IsLauncher(string path)
            => path.Equals(InstallLayout.LauncherFileName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Compares the release with what is installed. A file is fetched when its hash is not the
        /// one installed or it is gone from the disk or its size changed under it; a file the old
        /// release brought and the new one does not is removed. Nothing else in the folder is
        /// touched: the databases, the settings and the logs are the emulator's own.
        /// </summary>
        public static UpdatePlan For(ReleaseManifest release, InstallState installed, string root, long fullZipSize)
        {
            var plan = new UpdatePlan();
            foreach (var file in release.Files)
            {
                string local = System.IO.Path.Combine(root, file.Path);
                bool same = installed.Files.TryGetValue(file.Path, out var hash)
                            && hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)
                            && File.Exists(local) && new FileInfo(local).Length == file.Size;
                if (!same) plan.Fetch.Add(file);
            }
            var kept = new HashSet<string>(release.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            plan.Remove.AddRange(installed.Files.Keys.Where(p => !kept.Contains(p)));

            // A file that cannot be had on its own -- neither raw nor an asset of its own -- comes
            // only in the full zip.
            bool alone = plan.Fetch.All(f => f.Raw || f.Asset.Length > 0);
            plan.Full = installed.Files.Count == 0
                        || !alone
                        || plan.Fetch.Count > MaxSingleFiles
                        || (fullZipSize > 0 && plan.BytesToFetch > fullZipSize * FullShare);
            return plan;
        }

        /// <summary>A file's SHA-256 in lowercase hex.</summary>
        public static string HashOf(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
    }
}
