using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Cytrus;

namespace Jondo.Unity.Launcher.Packs
{
    /// <summary>
    /// The Cytrus manifest of one client version, fetched from Ankama's CDN and kept on disk.
    /// </summary>
    /// <remarks>
    /// The cache is <c>%LOCALAPPDATA%\Jondo\cytrus\6.0_&lt;version&gt;.manifest</c>: keyed by the
    /// version, so a client that moves to another version never reads the old one. When a new
    /// version's manifest arrives, the others are deleted; they are about 50 MB each.
    ///
    /// There is no fallback to another version. If the CDN does not serve the manifest for the
    /// client's version, the operation stops and says so.
    /// </remarks>
    internal sealed class ManifestStore
    {
        public const string Game = "dofus";
        public const string Release = "dofus3";
        public const string Platform = "windows";

        private readonly HttpClient _http;
        private readonly string _folder;

        public ManifestStore(HttpClient http, string folder)
        {
            _http = http;
            _folder = folder;
        }

        public static string DefaultFolder { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jondo", "cytrus");

        /// <summary>How long a transfer may go without a single byte before it is abandoned.</summary>
        public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

        /// <summary>The CDN's name for a client version: 3.6.10.11 is <c>6.0_3.6.10.11</c>.</summary>
        public static string ReleaseName(string version) => CytrusCdn.ReleasePrefix + version;

        public static string UrlFor(string version)
            => CytrusCdn.ManifestUrl(Game, Release, Platform, ReleaseName(version));

        public string PathFor(string version) => Path.Combine(_folder, ReleaseName(version) + ".manifest");

        /// <summary>The manifest for <paramref name="version"/>, from the cache or from the CDN.</summary>
        public async Task<CytrusManifest> GetAsync(string version, ProgressMeter meter, CancellationToken cancel)
        {
            string file = PathFor(version);
            if (File.Exists(file))
            {
                try
                {
                    return CytrusManifest.Parse(await File.ReadAllBytesAsync(file, cancel));
                }
                catch (InvalidDataException)
                {
                    // Damaged on disk: fetch it again rather than fail forever.
                    File.Delete(file);
                }
            }

            Directory.CreateDirectory(_folder);
            string url = UrlFor(version);
            string half = file + ".download";

            try
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                stall.CancelAfter(StallTimeout);

                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
                if (!response.IsSuccessStatusCode)
                    throw new PackException(PackError.ManifestUnavailable, url + ": HTTP " + (int)response.StatusCode);

                long? expected = response.Content.Headers.ContentLength;
                meter.Start(PackPhase.Manifest, expected ?? 0);

                await using (var output = new FileStream(half, FileMode.Create, FileAccess.Write, FileShare.None))
                await using (var input = await response.Content.ReadAsStreamAsync(stall.Token))
                {
                    var buffer = new byte[81920];
                    while (true)
                    {
                        stall.CancelAfter(StallTimeout);
                        int read = await input.ReadAsync(buffer, stall.Token);
                        if (read == 0) break;
                        await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                        meter.Add(read);
                    }

                    if (expected is long length && output.Length != length)
                        throw new PackException(PackError.ManifestUnavailable,
                            url + ": got " + output.Length + " bytes of " + length);
                }
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                TryDelete(half);
                throw new PackException(PackError.ManifestUnavailable, url + ": the transfer stalled");
            }
            catch (HttpRequestException ex)
            {
                TryDelete(half);
                throw new PackException(PackError.ManifestUnavailable, url + ": " + ex.Message, ex);
            }
            catch
            {
                TryDelete(half);
                throw;
            }

            CytrusManifest manifest;
            try
            {
                manifest = CytrusManifest.Parse(await File.ReadAllBytesAsync(half, cancel));
            }
            catch (InvalidDataException ex)
            {
                TryDelete(half);
                throw new PackException(PackError.ManifestCorrupt, url, ex);
            }

            File.Move(half, file, overwrite: true);
            PruneOthers(file);
            return manifest;
        }

        /// <summary>Deletes the manifests of other versions: they no longer match the client.</summary>
        private void PruneOthers(string keep)
        {
            try
            {
                foreach (string other in Directory.GetFiles(_folder, CytrusCdn.ReleasePrefix + "*.manifest"))
                {
                    if (!string.Equals(Path.GetFullPath(other), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
                        TryDelete(other);
                }
            }
            catch (IOException)
            {
                // Leftovers cost disk space, not correctness: they are never read.
            }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
