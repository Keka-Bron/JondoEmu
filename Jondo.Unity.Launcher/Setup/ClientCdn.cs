using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Cytrus;
using Jondo.Unity.Launcher.Packs;

namespace Jondo.Unity.Launcher.Setup
{
    /// <summary>Where a download from the CDN is, for the installer's bar.</summary>
    /// <param name="Fragment">The part of the client being worked on, as the CDN names it.</param>
    /// <param name="Fraction">The whole client, from 0 to 1.</param>
    /// <param name="Total">The client's size in bytes.</param>
    internal readonly record struct ClientCdnProgress(string Fragment, PackPhase Phase, double Fraction, long Total, double BytesPerSecond);

    /// <summary>
    /// The Dofus client from Ankama's own CDN, the one its launcher installs from: every file of
    /// the version the emulator needs, rebuilt in place from the CDN's bundles and checked
    /// against its SHA-1.
    /// </summary>
    /// <remarks>
    /// Nothing is packed, so it takes only the room the game itself takes (a 7z would need the
    /// archive and the unpacked game side by side); the CDN has no quota; and an interrupted
    /// download resumes where it stopped. Files already in the folder with the manifest's hash
    /// are kept, so a retry, or a folder that already holds part of the client, downloads only
    /// what is missing.
    ///
    /// Either platform's client can be fetched, from either system. Each needs a folder of its
    /// own: the two share every asset fragment, but their executable parts (win32_x64, linux)
    /// write different files under the same names in <c>Dofus_Data</c> (global-metadata.dat,
    /// globalgamemanagers, the addressables catalogue…), so one rebuilt over the other leaves
    /// the first unable to start. <see cref="FolderName"/> keeps them apart and
    /// <see cref="FetchAsync"/> refuses a folder that holds the other one.
    ///
    /// The scenery texture packs (HD, 4K) are left out, as Ankama's launcher leaves them out with
    /// its checkboxes off; the settings add them later. The CDN keeps serving old versions; if it
    /// ever stops serving this one, <see cref="IsServedAsync"/> says so and the installer takes
    /// the README's link instead.
    /// </remarks>
    internal static class ClientCdn
    {
        /// <summary>Inside the client's folder while it downloads: journals and finished-part markers.</summary>
        public const string WorkFolder = ".jondo-setup";

        /// <summary>The CDN's name for the Windows client.</summary>
        public const string Windows = "windows";

        /// <summary>The CDN's name for the native Linux client.</summary>
        public const string Linux = "linux";

        /// <summary>The platforms a client can be downloaded for, in the order the window shows them.</summary>
        public static IReadOnlyList<string> Platforms { get; } = new[] { Windows, Linux };

        /// <summary>The client this machine runs natively.</summary>
        public static string HostPlatform => OperatingSystem.IsWindows() ? Windows : Linux;

        /// <summary>
        /// Whether a client for <paramref name="platform"/> can be played from this machine: its own,
        /// and the Windows one on Linux through Wine. A Linux client on Windows can only be stored.
        /// </summary>
        public static bool RunsHere(string platform)
            => platform == HostPlatform || (platform == Windows && !OperatingSystem.IsWindows());

        /// <summary>
        /// The executable each platform's client starts from. The two manifests share every file
        /// but these and their configuration: Windows has <c>Dofus.exe</c>, Linux <c>Dofus.x64</c>
        /// with <c>GameAssembly.so</c>, and each its own start script.
        /// </summary>
        public static string ExecutableFor(string platform) => platform == Windows ? "Dofus.exe" : "Dofus.x64";

        /// <summary>The platform of a client's executable, by its name.</summary>
        public static string PlatformOf(string executable)
            => executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Windows : Linux;

        /// <summary>
        /// The executable the player meant when choosing a file of a client: Linux clients start
        /// from <c>zaap-start.sh</c> in Ankama's launcher, but it only runs <c>Dofus.x64</c>
        /// beside it, which is what this launcher starts with its own arguments.
        /// </summary>
        public static string ExecutableChosen(string path)
        {
            if (!Path.GetFileName(path).Equals(StartScript, StringComparison.OrdinalIgnoreCase)) return path;
            string sibling = Path.Combine(Path.GetDirectoryName(path) ?? "", ExecutableFor(Linux));
            return File.Exists(sibling) ? sibling : path;
        }

        /// <summary>Ankama's start script for the Linux client.</summary>
        public const string StartScript = "zaap-start.sh";

        /// <summary>
        /// The folder a downloaded client goes in: «Cliente 3.6.10.11» for Windows, as it always
        /// was, and «Cliente 3.6.10.11 Linux» for Linux, so that both can sit side by side.
        /// </summary>
        public static string FolderName(string version, string platform)
            => platform == Windows ? $"Cliente {version}" : $"Cliente {version} Linux";

        /// <summary>The platform whose client is in <paramref name="folder"/>, by its executable; null when none is.</summary>
        public static string? PlatformIn(string folder)
        {
            foreach (string platform in Platforms)
                if (File.Exists(Path.Combine(folder, ExecutableFor(platform)))) return platform;
            return null;
        }

        /// <summary>
        /// The client of <paramref name="platform"/> made whole in <paramref name="folder"/>: the
        /// version's manifest read (or taken from the cache), and every file missing or damaged
        /// fetched. Returns its executable.
        /// </summary>
        /// <remarks>
        /// The download puts the version file back as Ankama ships it. A client this machine plays
        /// through it (on Linux, natively or through Wine; on Windows only if it was pointed at the
        /// emulator before) gets the server's address written again.
        /// </remarks>
        public static async Task<string> FetchAsync(HttpClient http, string version, string platform, string folder,
                                                    IProgress<PackProgress>? reading, IProgress<ClientCdnProgress>? progress,
                                                    CancellationToken cancel)
        {
            string target = Path.GetFullPath(folder);
            string executable = Path.Combine(target, ExecutableFor(platform));
            string other = platform == Windows ? Linux : Windows;
            if (File.Exists(Path.Combine(target, ExecutableFor(other))))
                throw new SetupException($"«{target}» holds the {other} client; each platform's client needs a folder of its own.");

            bool pointed = PointsAtTheEmulator(target);
            var manifest = await ManifestAsync(http, version, platform, reading, cancel);
            await DownloadAsync(http, Fragments(manifest), target, progress, cancel);
            if (!File.Exists(executable)) throw new SetupException("The CDN's files did not make a Dofus client.");

            if (RunsHere(platform) && (!OperatingSystem.IsWindows() || pointed))
                new ClientInstallation(executable).Configure();
            return executable;
        }

        /// <summary>Whether the client in a folder already reads the emulator's address from its version file.</summary>
        private static bool PointsAtTheEmulator(string folder)
        {
            try
            {
                return ClientInstallation.InDirectory(folder) is { } client && File.Exists(client.VersionFile)
                       && client.Setting("ConfigUrl") == ClientInstallation.ConfigUrl;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Whether a fragment of the manifest is part of a playable client: all but the texture packs.</summary>
        public static bool IsPartOfTheClient(string fragment)
            => !TexturePack.All.Any(p => p.Fragment.Equals(fragment, StringComparison.Ordinal));

        public static IReadOnlyList<CytrusFragment> Fragments(CytrusManifest manifest)
            => manifest.Fragments().Where(f => IsPartOfTheClient(f.Name)).ToList();

        public static long Size(CytrusManifest manifest) => Fragments(manifest).Sum(f => f.Size);

        /// <summary>
        /// Room the download needs: the files themselves, nothing twice, and a margin for what
        /// MelonLoader writes on its first run.
        /// </summary>
        public static long SpaceNeeded(long size) => size + (1L << 30);

        /// <summary>
        /// Whether the CDN serves that version for that platform: sixteen bytes of its manifest, not its 50 MB. A
        /// refusal (403, 404) is a no; the network failing throws.
        /// </summary>
        public static async Task<bool> IsServedAsync(HttpClient http, string version, string platform, CancellationToken cancel)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ManifestStore.UrlFor(version, platform));
            request.Headers.Range = new RangeHeaderValue(0, 15);
            using var answer = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
            if ((int)answer.StatusCode >= 500) answer.EnsureSuccessStatusCode();
            return answer.IsSuccessStatusCode;
        }

        /// <summary>
        /// The version's manifest, kept in the launcher's own cache: the texture packs read the
        /// same one later, so it is fetched once.
        /// </summary>
        public static Task<CytrusManifest> ManifestAsync(HttpClient http, string version, string platform,
                                                        IProgress<PackProgress>? progress, CancellationToken cancel)
            => new ManifestStore(http, ManifestStore.DefaultFolder, platform).GetAsync(version, new ProgressMeter(progress), cancel);

        /// <summary>
        /// Puts every file of <paramref name="fragments"/> under <paramref name="folder"/>, one
        /// fragment after the other; a fragment already finished is not looked at again.
        /// </summary>
        public static async Task DownloadAsync(HttpClient http, IReadOnlyList<CytrusFragment> fragments, string folder,
                                               IProgress<ClientCdnProgress>? progress, CancellationToken cancel)
        {
            string target = Path.GetFullPath(folder);
            string prefix = target + Path.DirectorySeparatorChar;
            foreach (var file in fragments.SelectMany(f => f.Files))
            {
                // The names come from the network; none may land outside the client's folder.
                string path = Path.GetFullPath(Path.Combine(target, Local(file.Name)));
                if (!path.StartsWith(prefix, ClientInstallation.PathComparison))
                    throw new SetupException($"The CDN's manifest names a file outside the client's folder: «{file.Name}».");
            }

            string work = Path.Combine(target, WorkFolder);
            Directory.CreateDirectory(work);
            long total = fragments.Sum(f => f.Size);
            long before = 0;
            var downloader = new PackDownloader(http);

            foreach (var fragment in fragments)
            {
                cancel.ThrowIfCancellationRequested();
                string identity = PackDownloader.Identity(fragment.Files);
                string marker = Path.Combine(work, fragment.Name + ".done");
                if (File.Exists(marker) && File.ReadAllText(marker).Trim() == identity && AllThere(target, fragment))
                {
                    before += fragment.Size;
                    progress?.Report(new ClientCdnProgress(fragment.Name, PackPhase.Verifying, Fraction(before, 0, 0, total), total, 0));
                    continue;
                }

                long start = before;
                var relay = new Relay(p => progress?.Report(new ClientCdnProgress(
                    fragment.Name, p.Phase, Fraction(start, fragment.Size, Within(p), total), total, p.BytesPerSecond)));
                var meter = new ProgressMeter(relay);

                // What is there already and right stays; only the rest is fetched.
                var missing = new List<CytrusFile>();
                var present = fragment.Files.Where(f => Exists(target, f)).ToHashSet();
                meter.Start(PackPhase.Checking, present.Sum(f => f.Size));
                foreach (var file in fragment.Files)
                {
                    if (present.Contains(file)
                        && await PackDownloader.HashFileAsync(Path.Combine(target, Local(file.Name)), meter, cancel) == file.Hash)
                        continue;
                    missing.Add(file);
                }

                if (missing.Count > 0)
                    await downloader.DownloadAsync(target, Path.Combine(work, fragment.Name), fragment, missing, meter, cancel);

                await File.WriteAllTextAsync(marker, identity, cancel);
                before += fragment.Size;
                progress?.Report(new ClientCdnProgress(fragment.Name, PackPhase.Verifying, Fraction(before, 0, 0, total), total, 0));
            }

            // Rebuilt files come out without the execute bit; the manifest says which need it
            // (Dofus.x64 and zaap-start.sh). NTFS has no such bit: a Linux client stored from
            // Windows gets it from the launcher's own check when it is first started on Linux.
            if (!OperatingSystem.IsWindows())
            {
                foreach (var file in fragments.SelectMany(f => f.Files).Where(f => f.Executable))
                {
                    string path = Path.Combine(target, Local(file.Name));
                    if (File.Exists(path))
                        File.SetUnixFileMode(path, File.GetUnixFileMode(path)
                                                   | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                }
            }

            try { Directory.Delete(work, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        /// <summary>How far into its own fragment a report is: the download is the long part.</summary>
        private static double Within(PackProgress p) => p.Phase switch
        {
            PackPhase.Checking => 0.1 * p.Fraction,
            PackPhase.Downloading => 0.1 + 0.8 * p.Fraction,
            PackPhase.Verifying => 0.9 + 0.1 * p.Fraction,
            _ => 0,
        };

        private static double Fraction(long before, long size, double within, long total)
            => total <= 0 ? 1 : Math.Clamp((before + size * within) / total, 0, 1);

        private static string Local(string name) => name.Replace('/', Path.DirectorySeparatorChar);

        private static bool Exists(string root, CytrusFile file)
        {
            var info = new FileInfo(Path.Combine(root, Local(file.Name)));
            return info.Exists && info.Length == file.Size;
        }

        /// <summary>A fragment marked finished still has every file, at its size.</summary>
        private static bool AllThere(string root, CytrusFragment fragment) => fragment.Files.All(f => Exists(root, f));

        /// <summary>Hands each report straight on: the installer's own progress posts it to the window.</summary>
        private sealed class Relay : IProgress<PackProgress>
        {
            private readonly Action<PackProgress> _report;
            public Relay(Action<PackProgress> report) => _report = report;
            public void Report(PackProgress value) => _report(value);
        }
    }
}
