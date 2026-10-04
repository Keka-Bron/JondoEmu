using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Cytrus;

namespace Jondo.Unity.Launcher.Packs
{
    /// <summary>
    /// The HD and 4K scenery packs of one client folder: what is on disk, installing, removing,
    /// and which launch arguments they allow.
    /// </summary>
    /// <remarks>
    /// "Installed" means one thing only: every file of the pack was hashed against the manifest
    /// of the version the client says it is, and that is recorded in a marker file in the pack
    /// folder (<see cref="MarkerName"/>) together with the version. A marker for another version
    /// is stale: the pack shows as unverified and its launch argument is not passed until it is
    /// verified again. Files installed by Ankama's launcher start out unverified for the same
    /// reason; "Verify" hashes them and, if they are all right, downloads nothing.
    ///
    /// The client's own <c>currentTexturePack</c> setting is not touched: which number means
    /// which pack is not known yet, and the client rewrites that file while it runs. The player
    /// picks the pack in the game's options.
    /// </remarks>
    internal sealed class TexturePackService
    {
        public const string MarkerName = "jondo-pack.json";

        /// <summary>Kept free on top of the pack itself, so the download never fills the disk.</summary>
        private const long SpaceMargin = 256L << 20;

        private static readonly Lazy<HttpClient> SharedHttp = new(() =>
        {
            // No overall timeout: bundles are tens of megabytes on slow lines. Transfers are cut
            // when they stall instead, see PackDownloader.StallTimeout.
            var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Jondo/1.0");
            return http;
        });

        private readonly HttpClient _http;

        public TexturePackService(string clientRoot, string cacheFolder, HttpClient? http = null)
        {
            ClientRoot = clientRoot ?? "";
            CacheFolder = cacheFolder;
            _http = http ?? SharedHttp.Value;
        }

        /// <summary>The service for the client the launcher starts, or for no client at all.</summary>
        public static TexturePackService ForClient(string dofusExe)
            => new(dofusExe.Length > 0 ? Path.GetDirectoryName(dofusExe) ?? "" : "", ManifestStore.DefaultFolder);

        public string ClientRoot { get; }

        public string CacheFolder { get; }

        /// <summary>Whether a Dofus.exe from this folder is running. Replaceable for tests.</summary>
        public Func<string, bool> IsClientRunning { get; init; } = ClientRunningFrom;

        /// <summary>Free bytes on the disk holding a path, or null if unknown. Replaceable for tests.</summary>
        public Func<string, long?> FreeSpace { get; init; } = FreeSpaceAt;

        /// <summary>How downloads are done. Replaceable for tests (fewer attempts, no waiting).</summary>
        public Func<HttpClient, PackDownloader> Downloader { get; init; } = http => new PackDownloader(http);

        public string ClientVersion => Packs.ClientVersion.Read(ClientRoot) ?? "";

        public string PackFolder(TexturePack pack) => Path.Combine(ClientRoot, pack.RelativeFolder);

        /// <summary>The manifest URL for this client, from its version file. Empty if unreadable.</summary>
        public string ManifestUrl
        {
            get
            {
                string version = ClientVersion;
                return version.Length == 0 ? "" : ManifestStore.UrlFor(version);
            }
        }

        // ─── What is on disk ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The pack's state, from the file system alone: no network, no manifest, no hashing.
        /// </summary>
        public PackStatus Status(TexturePack pack)
        {
            if (ClientRoot.Length == 0 || !File.Exists(Path.Combine(ClientRoot, "Dofus.exe")))
                return new PackStatus(pack, PackState.NoClient, "", "", null);

            string version = ClientVersion;
            string folder = PackFolder(pack);
            var marker = ReadMarker(folder);
            long? size = marker?.Files.Sum(f => f.Size);
            string verified = marker?.Version ?? "";

            if (version.Length == 0) return new PackStatus(pack, PackState.NoVersion, "", verified, size);
            if (!Directory.Exists(folder)) return new PackStatus(pack, PackState.Missing, version, "", size);

            bool interrupted = File.Exists(Path.Combine(folder, PackDownloader.JournalName))
                               || Directory.EnumerateFiles(folder, "*" + PackDownloader.PartSuffix).Any();
            bool markerForThisVersion = marker != null
                                        && marker.Version == version
                                        && marker.Fragment == pack.Fragment
                                        && marker.Files.Count > 0;
            bool allThere = markerForThisVersion && marker!.Files.All(f =>
            {
                var file = new FileInfo(Path.Combine(ClientRoot, f.Name.Replace('/', Path.DirectorySeparatorChar)));
                return file.Exists && file.Length == f.Size;
            });

            PackState state;
            if (interrupted) state = PackState.Partial;
            else if (allThere) state = PackState.Installed;
            else if (!Directory.EnumerateFiles(folder).Any(f => Path.GetFileName(f) != MarkerName)) state = PackState.Missing;
            else if (markerForThisVersion) state = PackState.Partial;
            else state = PackState.Unverified;

            return new PackStatus(pack, state, version, verified, size);
        }

        /// <summary>
        /// The launch arguments the packs allow: a pack's flag only when the player wants it AND
        /// the pack is installed and verified for the client's current version.
        /// </summary>
        public string LaunchFlags(bool useHd, bool use4k)
        {
            var flags = new List<string>();
            if (useHd && Status(TexturePack.Hd).State == PackState.Installed) flags.Add(TexturePack.Hd.LaunchFlag);
            if (use4k && Status(TexturePack.FourK).State == PackState.Installed) flags.Add(TexturePack.FourK.LaunchFlag);
            return string.Join(" ", flags);
        }

        // ─── Installing ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Makes the pack complete and verified for the client's version: hashes what is on
        /// disk, downloads what is missing or wrong, and writes the marker. It also serves as
        /// "Verify" and as "Resume".
        /// </summary>
        /// <returns>How many files had to be downloaded.</returns>
        public async Task<int> InstallAsync(TexturePack pack, IProgress<PackProgress>? progress, CancellationToken cancel)
        {
            var meter = new ProgressMeter(progress);
            string version = RequireVersion();
            RequireClientClosed();

            var manifest = await new ManifestStore(_http, CacheFolder).GetAsync(version, meter, cancel);
            var fragment = manifest.Fragment(pack.Fragment)
                           ?? throw new PackException(PackError.FragmentMissing, pack.Fragment + " in " + ManifestStore.ReleaseName(version));

            string folder = PackFolder(pack);
            string prefix = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
            foreach (var file in fragment.Files)
            {
                // The names come from the network; none may land outside the pack folder.
                string path = Path.GetFullPath(Path.Combine(ClientRoot, file.Name.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new PackException(PackError.BadManifest, file.Name + " is outside " + pack.RelativeFolder);
            }

            // What is already there is hashed, not trusted: a file of the right size can still
            // be damaged, and the marker vouches for every byte.
            var existing = fragment.Files
                .Where(f => new FileInfo(PathOf(f)).Exists && new FileInfo(PathOf(f)).Length == f.Size)
                .ToList();
            meter.Start(PackPhase.Checking, existing.Sum(f => f.Size));
            var targets = new List<CytrusFile>();
            foreach (var file in fragment.Files)
            {
                bool good = existing.Contains(file) && await PackDownloader.HashFileAsync(PathOf(file), meter, cancel) == file.Hash;
                if (!good) targets.Add(file);
            }

            if (targets.Count > 0)
            {
                // Known to be incomplete from here on, whatever it was before.
                TryDelete(Path.Combine(folder, MarkerName));
                RequireSpace(folder, targets);
                await Downloader(_http).DownloadAsync(ClientRoot, folder, fragment, targets, meter, cancel);
            }

            WriteMarker(folder, new PackMarker
            {
                Version = version,
                Fragment = pack.Fragment,
                Files = fragment.Files.Select(f => new PackMarker.Entry { Name = f.Name, Size = f.Size, Sha1 = f.Hash.ToString() }).ToList(),
            });
            return targets.Count;
        }

        /// <summary>Deletes the whole pack folder, finished or not.</summary>
        public void Remove(TexturePack pack)
        {
            RequireClientClosed();
            string folder = PackFolder(pack);
            if (!Directory.Exists(folder)) return;

            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PackException(PackError.DiskError, folder + ": " + ex.Message, ex);
            }
        }

        private string PathOf(CytrusFile file)
            => Path.Combine(ClientRoot, file.Name.Replace('/', Path.DirectorySeparatorChar));

        private string RequireVersion()
        {
            if (ClientRoot.Length == 0 || !File.Exists(Path.Combine(ClientRoot, "Dofus.exe")))
                throw new PackException(PackError.NoClient, ClientRoot);

            string version = ClientVersion;
            if (version.Length == 0)
                throw new PackException(PackError.NoVersion, Path.Combine(ClientRoot, Packs.ClientVersion.RelativePath));
            return version;
        }

        private void RequireClientClosed()
        {
            // Unity keeps the bundles it has loaded open; writing under a running client either
            // fails half way or, worse, succeeds under its feet.
            if (IsClientRunning(ClientRoot)) throw new PackException(PackError.ClientRunning, ClientRoot);
        }

        private void RequireSpace(string folder, IReadOnlyList<CytrusFile> targets)
        {
            // A .part that already has its full length is already paid for.
            long needed = SpaceMargin;
            foreach (var file in targets)
            {
                var part = new FileInfo(PathOf(file) + PackDownloader.PartSuffix);
                needed += Math.Max(0, file.Size - (part.Exists ? part.Length : 0));
            }

            long? free = FreeSpace(ClientRoot);
            if (free is long bytes && bytes < needed)
                throw new PackException(PackError.NotEnoughSpace, folder) { Needed = needed, Free = bytes };
        }

        private static long? FreeSpaceAt(string path)
        {
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(path));
                return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static bool ClientRunningFrom(string root)
        {
            if (root.Length == 0) return false;
            string wanted = Path.GetFullPath(Path.Combine(root, "Dofus.exe"));

            foreach (var process in Process.GetProcessesByName("Dofus"))
            {
                using (process)
                {
                    try
                    {
                        string? path = process.MainModule?.FileName;
                        if (path != null && string.Equals(Path.GetFullPath(path), wanted, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // Exited meanwhile, or not ours to look at: an elevated Dofus is not one
                        // this launcher started.
                    }
                }
            }
            return false;
        }

        // ─── The marker ──────────────────────────────────────────────────────────────────

        /// <summary>What was verified, for which client version.</summary>
        internal sealed class PackMarker
        {
            public string Version { get; set; } = "";
            public string Fragment { get; set; } = "";
            public List<Entry> Files { get; set; } = new();

            public sealed class Entry
            {
                public string Name { get; set; } = "";
                public long Size { get; set; }
                public string Sha1 { get; set; } = "";
            }
        }

        private static PackMarker? ReadMarker(string folder)
        {
            try
            {
                string path = Path.Combine(folder, MarkerName);
                return File.Exists(path) ? JsonSerializer.Deserialize<PackMarker>(File.ReadAllText(path)) : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void WriteMarker(string folder, PackMarker marker)
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, MarkerName);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, overwrite: true);
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
