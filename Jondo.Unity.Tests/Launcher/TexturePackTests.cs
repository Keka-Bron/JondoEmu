using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Cytrus;
using Jondo.Unity.Launcher;
using Jondo.Unity.Launcher.Packs;
using Xunit;

namespace Jondo.Unity.Tests.Launcher
{
    /// <summary>
    /// The HD/4K texture packs: the resumable downloader against a fake CDN, the version the
    /// manifest is chosen by, and the launch flags.
    /// </summary>
    /// <remarks>
    /// Nothing here touches the network or the real client folder's packs: every client is a
    /// temporary folder with an empty Dofus.exe and a version file.
    /// </remarks>
    public sealed class TexturePackTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "jondo-packs-" + Guid.NewGuid().ToString("N"));

        public TexturePackTests()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllBytes(Path.Combine(_root, "Dofus.exe"), Array.Empty<byte>());
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private string PackFolder => Path.Combine(_root, TexturePack.Hd.RelativeFolder);

        private void WriteVersion(string version)
        {
            string path = Path.Combine(_root, ClientVersion.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "Version=" + version + "\nBuildDate=2026-08-24T11:33:20+00:00\nConfigUrl=https://example\n");
        }

        private static PackDownloader Downloader(FakeCdn cdn, int concurrency = 4)
            => new(new HttpClient(cdn)) { Concurrency = concurrency, Backoff = _ => TimeSpan.Zero, Attempts = 3 };

        private void AssertInstalled(SyntheticPack data)
        {
            foreach (var file in data.Fragment.Files)
            {
                string path = Path.Combine(_root, file.Name);
                Assert.Equal(data.Files[file.Name], File.ReadAllBytes(path));
                Assert.False(File.Exists(path + PackDownloader.PartSuffix));
            }
            Assert.False(File.Exists(Path.Combine(PackFolder, PackDownloader.JournalName)));
        }

        // ─────────────────────────────────────────────────── the downloader

        [Fact]
        public async Task A_pack_is_rebuilt_from_its_bundles()
        {
            var data = SyntheticPack.Build();
            var cdn = new FakeCdn(data);

            await Downloader(cdn).DownloadAsync(_root, PackFolder, data.Fragment, data.Fragment.Files,
                                                new ProgressMeter(null), CancellationToken.None);

            AssertInstalled(data);
            Assert.Equal(3, cdn.Requests.Count);
            Assert.All(cdn.Requests, r => Assert.NotNull(r.Range));
        }

        [Fact]
        public async Task A_corrupt_chunk_is_rejected_and_its_bundle_fetched_again()
        {
            var data = SyntheticPack.Build();
            var bad = data.Fragment.Bundles[1].Hash;
            var cdn = new FakeCdn(data) { Corrupt = (hash, attempt) => hash == bad && attempt == 1 };

            await Downloader(cdn).DownloadAsync(_root, PackFolder, data.Fragment, data.Fragment.Files,
                                                new ProgressMeter(null), CancellationToken.None);

            AssertInstalled(data);
            Assert.Equal(2, cdn.Requests.Count(r => r.Bundle == bad));
        }

        [Fact]
        public async Task An_interrupted_download_resumes_from_the_journal()
        {
            var data = SyntheticPack.Build();
            var last = data.Fragment.Bundles[^1].Hash;

            // One bundle at a time so the failure is the last thing that happens; a 404 does not
            // fix itself, so it is not retried.
            var broken = new FakeCdn(data) { Status = hash => hash == last ? HttpStatusCode.NotFound : null };
            var error = await Assert.ThrowsAsync<PackException>(() =>
                Downloader(broken, concurrency: 1).DownloadAsync(_root, PackFolder, data.Fragment, data.Fragment.Files,
                                                                 new ProgressMeter(null), CancellationToken.None));
            Assert.Equal(PackError.DownloadFailed, error.Error);

            var journal = File.ReadAllLines(Path.Combine(PackFolder, PackDownloader.JournalName));
            Assert.Equal(3, journal.Length);   // the header and the two bundles that made it
            Assert.All(data.Fragment.Files, f => Assert.False(File.Exists(Path.Combine(_root, f.Name))));

            var healthy = new FakeCdn(data);
            await Downloader(healthy).DownloadAsync(_root, PackFolder, data.Fragment, data.Fragment.Files,
                                                    new ProgressMeter(null), CancellationToken.None);

            AssertInstalled(data);
            Assert.Equal(new[] { last }, healthy.Requests.Select(r => r.Bundle));
        }

        [Fact]
        public async Task Cancelling_leaves_a_state_that_resumes()
        {
            var data = SyntheticPack.Build();
            var second = data.Fragment.Bundles[1].Hash;
            using var cancel = new CancellationTokenSource();

            // The second bundle hangs until the player cancels, which it does itself.
            var hanging = new FakeCdn(data)
            {
                Hang = hash =>
                {
                    if (hash != second) return false;
                    cancel.Cancel();
                    return true;
                },
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Downloader(hanging, concurrency: 1).DownloadAsync(_root, PackFolder, data.Fragment, data.Fragment.Files,
                                                                  new ProgressMeter(null), cancel.Token));

            Assert.True(File.Exists(Path.Combine(PackFolder, PackDownloader.JournalName)));
            Assert.All(data.Fragment.Files, f => Assert.True(File.Exists(Path.Combine(_root, f.Name) + PackDownloader.PartSuffix)));

            var healthy = new FakeCdn(data);
            await Downloader(healthy).DownloadAsync(_root, PackFolder, data.Fragment, data.Fragment.Files,
                                                    new ProgressMeter(null), CancellationToken.None);

            AssertInstalled(data);
            Assert.DoesNotContain(data.Fragment.Bundles[0].Hash, healthy.Requests.Select(r => r.Bundle));
        }

        [Fact]
        public async Task A_file_is_hashed_whole_before_it_is_put_in_place()
        {
            var data = SyntheticPack.Build();

            // Every chunk is right, but the file's own hash in the manifest is not: the chunks
            // pass, and only the whole-file check can catch it.
            var lying = data.Fragment.Files[1] with { Hash = Sha1Hash.Of(new byte[] { 42 }) };
            var fragment = data.Fragment with { Files = new[] { data.Fragment.Files[0], lying } };

            var error = await Assert.ThrowsAsync<PackException>(() =>
                Downloader(new FakeCdn(data)).DownloadAsync(_root, PackFolder, fragment, fragment.Files,
                                                            new ProgressMeter(null), CancellationToken.None));

            Assert.Equal(PackError.VerifyFailed, error.Error);
            Assert.False(File.Exists(Path.Combine(_root, lying.Name)));
            Assert.False(File.Exists(Path.Combine(_root, lying.Name) + PackDownloader.PartSuffix));
            Assert.False(File.Exists(Path.Combine(PackFolder, PackDownloader.JournalName)));
        }

        // ─────────────────────────────────────────────────── the client's version

        [Fact]
        public void The_manifest_url_comes_from_the_clients_version_file()
        {
            WriteVersion("3.6.10.11");
            var service = new TexturePackService(_root, Path.Combine(_root, "cache"));

            Assert.Equal("3.6.10.11", service.ClientVersion);
            Assert.Equal("https://cytrus.cdn.ankama.com/dofus/releases/dofus3/windows/6.0_3.6.10.11.manifest",
                         service.ManifestUrl);
        }

        [Fact]
        public void The_real_clients_url_is_pinned_to_its_own_version_file()
        {
            // The client next to the emulator, if there is one. Its version file is read here
            // independently, so the test says which URL that client must use, whatever the
            // emulator's own version constant is.
            string client = Paths.ClientDir;
            string file = Path.Combine(client, "Dofus_Data", "StreamingAssets", "version");
            if (!File.Exists(Path.Combine(client, "Dofus.exe")) || !File.Exists(file)) return;

            string version = File.ReadAllLines(file).First(l => l.StartsWith("Version=", StringComparison.Ordinal))["Version=".Length..].Trim();
            var service = TexturePackService.ForClient(Path.Combine(client, "Dofus.exe"));

            Assert.Equal("https://cytrus.cdn.ankama.com/dofus/releases/dofus3/windows/6.0_" + version + ".manifest",
                         service.ManifestUrl);
            if (version != LauncherService.Version)
                Assert.DoesNotContain(LauncherService.Version + ".manifest", service.ManifestUrl);
        }

        [Fact]
        public async Task Without_a_version_file_nothing_is_downloaded()
        {
            var cdn = new FakeCdn(SyntheticPack.Build());
            var service = new TexturePackService(_root, Path.Combine(_root, "cache"), new HttpClient(cdn))
            {
                IsClientRunning = _ => false,
            };

            var error = await Assert.ThrowsAsync<PackException>(() =>
                service.InstallAsync(TexturePack.Hd, null, CancellationToken.None));

            Assert.Equal(PackError.NoVersion, error.Error);
            Assert.Empty(cdn.Requests);
            Assert.Equal(PackState.NoVersion, service.Status(TexturePack.Hd).State);
        }

        [Fact]
        public async Task A_manifest_the_cdn_does_not_serve_stops_without_trying_another_version()
        {
            WriteVersion("3.6.10.11");
            var cdn = new FakeCdn(SyntheticPack.Build());
            var service = new TexturePackService(_root, Path.Combine(_root, "cache"), new HttpClient(cdn))
            {
                IsClientRunning = _ => false,
            };

            var error = await Assert.ThrowsAsync<PackException>(() =>
                service.InstallAsync(TexturePack.Hd, null, CancellationToken.None));

            Assert.Equal(PackError.ManifestUnavailable, error.Error);
            Assert.Equal(new[] { "https://cytrus.cdn.ankama.com/dofus/releases/dofus3/windows/6.0_3.6.10.11.manifest" },
                         cdn.Others);
        }

        [Fact]
        public async Task A_running_client_is_not_written_under()
        {
            WriteVersion("3.6.10.11");
            var cdn = new FakeCdn(SyntheticPack.Build());
            var service = new TexturePackService(_root, Path.Combine(_root, "cache"), new HttpClient(cdn))
            {
                IsClientRunning = _ => true,
            };

            var error = await Assert.ThrowsAsync<PackException>(() =>
                service.InstallAsync(TexturePack.Hd, null, CancellationToken.None));

            Assert.Equal(PackError.ClientRunning, error.Error);
            Assert.Empty(cdn.Others);
        }

        // ─────────────────────────────────────────────────── the launch flags

        /// <summary>Puts the synthetic pack on disk as a verified pack of <paramref name="verifiedFor"/>.</summary>
        private void InstallVerified(SyntheticPack data, string verifiedFor)
        {
            foreach (var (name, bytes) in data.Files)
            {
                string path = Path.Combine(_root, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
            }

            var marker = new TexturePackService.PackMarker
            {
                Version = verifiedFor,
                Fragment = TexturePack.Hd.Fragment,
                Files = data.Fragment.Files
                    .Select(f => new TexturePackService.PackMarker.Entry { Name = f.Name, Size = f.Size, Sha1 = f.Hash.ToString() })
                    .ToList(),
            };
            File.WriteAllText(Path.Combine(PackFolder, TexturePackService.MarkerName), JsonSerializer.Serialize(marker));
        }

        private string Arguments(TexturePackService service, bool hd, bool fourK)
            => LauncherService.ClientArguments(1920, 1080, 1, "hash", "es", service.LaunchFlags(hd, fourK));

        [Fact]
        public void The_flag_is_passed_only_when_wanted_and_the_pack_is_verified_for_this_version()
        {
            WriteVersion("3.6.10.11");
            var service = new TexturePackService(_root, Path.Combine(_root, "cache"));

            // Nothing on disk: wanting it changes nothing.
            Assert.Equal(PackState.Missing, service.Status(TexturePack.Hd).State);
            Assert.DoesNotContain("Ready", Arguments(service, hd: true, fourK: true));

            InstallVerified(SyntheticPack.Build(), "3.6.10.11");
            Assert.Equal(PackState.Installed, service.Status(TexturePack.Hd).State);

            Assert.EndsWith("--connectionPort 5555 --hdReady", Arguments(service, hd: true, fourK: true));
            Assert.DoesNotContain("--hdReady", Arguments(service, hd: false, fourK: true));
            Assert.DoesNotContain("--4kReady", Arguments(service, hd: true, fourK: true));
        }

        [Fact]
        public void A_pack_verified_for_another_client_version_is_not_offered_until_verified_again()
        {
            WriteVersion("3.6.10.11");
            InstallVerified(SyntheticPack.Build(), "3.6.10.10");
            var service = new TexturePackService(_root, Path.Combine(_root, "cache"));

            var status = service.Status(TexturePack.Hd);
            Assert.Equal(PackState.Unverified, status.State);
            Assert.Equal("3.6.10.10", status.VerifiedVersion);
            Assert.Equal("", service.LaunchFlags(true, true));
        }

        [Fact]
        public void A_verified_pack_with_a_file_gone_is_not_offered()
        {
            WriteVersion("3.6.10.11");
            var data = SyntheticPack.Build();
            InstallVerified(data, "3.6.10.11");
            File.Delete(Path.Combine(_root, data.Fragment.Files[0].Name));
            var service = new TexturePackService(_root, Path.Combine(_root, "cache"));

            Assert.Equal(PackState.Partial, service.Status(TexturePack.Hd).State);
            Assert.Equal("", service.LaunchFlags(true, true));
        }

        [Fact]
        public void An_interrupted_download_is_not_offered_even_with_an_old_marker()
        {
            WriteVersion("3.6.10.11");
            var data = SyntheticPack.Build();
            InstallVerified(data, "3.6.10.11");
            File.WriteAllText(Path.Combine(PackFolder, PackDownloader.JournalName), "x");
            var service = new TexturePackService(_root, Path.Combine(_root, "cache"));

            Assert.Equal(PackState.Partial, service.Status(TexturePack.Hd).State);
            Assert.Equal("", service.LaunchFlags(true, false));
        }

        [Fact]
        public void Files_nobody_verified_are_not_offered()
        {
            // What Ankama's launcher leaves: the files, without our marker.
            WriteVersion("3.6.10.11");
            var data = SyntheticPack.Build();
            InstallVerified(data, "3.6.10.11");
            File.Delete(Path.Combine(PackFolder, TexturePackService.MarkerName));
            var service = new TexturePackService(_root, Path.Combine(_root, "cache"));

            Assert.Equal(PackState.Unverified, service.Status(TexturePack.Hd).State);
            Assert.Equal("", service.LaunchFlags(true, false));
        }
    }

    /// <summary>
    /// A small made-up pack: two files that share a chunk, three bundles, and one chunk in a
    /// bundle that no file wants, so the downloader has to read past it.
    /// </summary>
    internal sealed class SyntheticPack
    {
        public CytrusFragment Fragment { get; private set; } = null!;
        public Dictionary<string, byte[]> Files { get; } = new();
        public Dictionary<Sha1Hash, byte[]> Chunks { get; } = new();
        public Dictionary<Sha1Hash, byte[]> Bundles { get; } = new();

        public static SyntheticPack Build()
        {
            var random = new Random(7);
            byte[] Chunk(int size)
            {
                var bytes = new byte[size];
                random.NextBytes(bytes);
                return bytes;
            }

            byte[] c0 = Chunk(1000), c1 = Chunk(1500), c2 = Chunk(700), c3 = Chunk(1200), c4 = Chunk(900);
            byte[] unwanted = Chunk(333);

            var pack = new SyntheticPack();
            foreach (var c in new[] { c0, c1, c2, c3, c4 }) pack.Chunks[Sha1Hash.Of(c)] = c;

            const string folder = "Dofus_Data/StreamingAssets/Content/Map/Textures/2x/";
            CytrusFile MakeFile(string name, params byte[][] chunks)
            {
                var body = chunks.SelectMany(c => c).ToArray();
                pack.Files[folder + name] = body;
                long at = 0;
                var listed = chunks.Select(c =>
                {
                    var chunk = new CytrusChunk(Sha1Hash.Of(c), c.Length, at);
                    at += c.Length;
                    return chunk;
                }).ToArray();
                return new CytrusFile(folder + name, body.Length, Sha1Hash.Of(body), listed, false);
            }

            CytrusBundle MakeBundle(params byte[][] chunks)
            {
                var body = chunks.SelectMany(c => c).ToArray();
                long at = 0;
                var listed = chunks.Select(c =>
                {
                    var chunk = new CytrusChunk(Sha1Hash.Of(c), c.Length, at);
                    at += c.Length;
                    return chunk;
                }).ToArray();
                var hash = Sha1Hash.Of(body);
                pack.Bundles[hash] = body;
                return new CytrusBundle(hash, listed);
            }

            var files = new[] { MakeFile("a.bundle", c0, c1, c2), MakeFile("b.bundle", c3, c1, c4) };
            var bundles = new[] { MakeBundle(c0, c1), MakeBundle(c2, c3), MakeBundle(unwanted, c4) };
            pack.Fragment = new CytrusFragment("map_textures_2x", files, bundles);
            return pack;
        }
    }

    /// <summary>
    /// Ankama's CDN, faked: serves the synthetic bundles with range requests, and can corrupt,
    /// refuse or hang a bundle on demand. Anything that is not a bundle gets a 404.
    /// </summary>
    internal sealed class FakeCdn : HttpMessageHandler
    {
        private readonly SyntheticPack _pack;
        private readonly ConcurrentDictionary<Sha1Hash, int> _attempts = new();

        public FakeCdn(SyntheticPack pack) => _pack = pack;

        public ConcurrentQueue<(Sha1Hash Bundle, System.Net.Http.Headers.RangeHeaderValue? Range)> Requests { get; } = new();

        /// <summary>Every request that was not for a bundle, such as manifests.</summary>
        public ConcurrentQueue<string> Others { get; } = new();

        public Func<Sha1Hash, int, bool> Corrupt { get; init; } = (_, _) => false;
        public Func<Sha1Hash, HttpStatusCode?> Status { get; init; } = _ => null;
        public Func<Sha1Hash, bool> Hang { get; init; } = _ => false;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            string url = request.RequestUri!.ToString();
            string last = url[(url.LastIndexOf('/') + 1)..];
            Sha1Hash hash = last.Length == 40 && last.All(Uri.IsHexDigit) ? Sha1Hash.Parse(last) : default;

            if (hash.IsEmpty || !_pack.Bundles.TryGetValue(hash, out var body)
                || url != CytrusCdn.BundleUrl("dofus", hash))
            {
                Others.Enqueue(url);
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            Requests.Enqueue((hash, request.Headers.Range));
            int attempt = _attempts.AddOrUpdate(hash, 1, (_, n) => n + 1);

            if (Status(hash) is HttpStatusCode code) return new HttpResponseMessage(code);
            if (Hang(hash)) await Task.Delay(Timeout.Infinite, cancel);

            var range = request.Headers.Range?.Ranges.Single();
            long from = range?.From ?? 0;
            long to = range?.To ?? body.Length - 1;
            var slice = body.AsSpan((int)from, (int)(to - from + 1)).ToArray();
            if (Corrupt(hash, attempt)) slice[slice.Length / 2] ^= 0xFF;

            var response = new HttpResponseMessage(range != null ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(slice),
            };
            if (range != null)
                response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, body.Length);
            return response;
        }
    }
}
