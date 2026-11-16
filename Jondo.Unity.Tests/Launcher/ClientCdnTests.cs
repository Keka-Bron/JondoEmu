using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Cytrus;
using Jondo.Unity.Launcher;
using Jondo.Unity.Launcher.Setup;
using Xunit;

namespace Jondo.Unity.Tests.Launcher
{
    /// <summary>
    /// The installer's client from Ankama's CDN, against the fake CDN of the texture-pack tests:
    /// files rebuilt in place, what is already right kept, a finished part not fetched again, and
    /// nothing written outside the client's folder.
    /// </summary>
    public sealed class ClientCdnTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "jondo-cdn-client-" + Guid.NewGuid().ToString("N"));

        public ClientCdnTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private string Local(string name) => Path.Combine(_root, name.Replace('/', Path.DirectorySeparatorChar));

        private void AssertRebuilt(SyntheticPack data, CytrusFragment fragment)
        {
            foreach (var file in fragment.Files)
            {
                string original = data.Fragment.Files.Single(f => f.Hash == file.Hash && f.Size == file.Size).Name;
                Assert.Equal(data.Files[original], File.ReadAllBytes(Local(file.Name)));
                Assert.False(File.Exists(Local(file.Name) + Jondo.Unity.Launcher.Packs.PackDownloader.PartSuffix));
            }
        }

        /// <summary>The same bytes under other names: a second part of the client served by the same bundles.</summary>
        private static CytrusFragment Renamed(CytrusFragment fragment, string name, string folder)
            => new(name, fragment.Files.Select(f => f with { Name = folder + "/" + Path.GetFileName(f.Name) }).ToArray(), fragment.Bundles);

        [Fact]
        public async Task The_client_is_rebuilt_in_its_folder_from_the_cdn()
        {
            var data = SyntheticPack.Build();
            var cdn = new FakeCdn(data);

            await ClientCdn.DownloadAsync(new HttpClient(cdn), new[] { data.Fragment }, _root, null, CancellationToken.None);

            AssertRebuilt(data, data.Fragment);
            Assert.Equal(3, cdn.Requests.Count);
            Assert.False(Directory.Exists(Path.Combine(_root, ClientCdn.WorkFolder)));
        }

        [Fact]
        public async Task Files_already_right_are_kept_and_only_the_rest_is_fetched()
        {
            var data = SyntheticPack.Build();
            var cdn = new FakeCdn(data);
            var kept = data.Fragment.Files[1];
            Directory.CreateDirectory(Path.GetDirectoryName(Local(kept.Name))!);
            File.WriteAllBytes(Local(kept.Name), data.Files[kept.Name]);

            await ClientCdn.DownloadAsync(new HttpClient(cdn), new[] { data.Fragment }, _root, null, CancellationToken.None);

            AssertRebuilt(data, data.Fragment);
            // The first file's chunks are in the first two bundles; the third only serves the kept one.
            Assert.Equal(2, cdn.Requests.Count);
            Assert.DoesNotContain(cdn.Requests, r => r.Bundle == data.Fragment.Bundles[2].Hash);
        }

        [Fact]
        public async Task A_damaged_file_of_the_right_size_is_fetched_again()
        {
            var data = SyntheticPack.Build();
            var cdn = new FakeCdn(data);
            var damaged = data.Fragment.Files[0];
            Directory.CreateDirectory(Path.GetDirectoryName(Local(damaged.Name))!);
            File.WriteAllBytes(Local(damaged.Name), new byte[damaged.Size]);

            await ClientCdn.DownloadAsync(new HttpClient(cdn), new[] { data.Fragment }, _root, null, CancellationToken.None);

            AssertRebuilt(data, data.Fragment);
        }

        [Fact]
        public async Task A_part_finished_before_an_interruption_is_not_fetched_again()
        {
            var data = SyntheticPack.Build();
            var cdn = new FakeCdn(data);
            var second = Renamed(data.Fragment, "second", "Dofus_Data/Second");
            var parts = new[] { data.Fragment, second };

            // Stopped as soon as the second part starts.
            using var stop = new CancellationTokenSource();
            var progress = new Inline(p => { if (p.Fragment == "second") stop.Cancel(); });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => ClientCdn.DownloadAsync(new HttpClient(cdn), parts, _root, progress, stop.Token));
            int first = cdn.Requests.Count;
            Assert.Equal(3, first);

            await ClientCdn.DownloadAsync(new HttpClient(cdn), parts, _root, null, CancellationToken.None);

            AssertRebuilt(data, data.Fragment);
            AssertRebuilt(data, second);
            Assert.Equal(first + 3, cdn.Requests.Count);
        }

        [Fact]
        public async Task A_manifest_naming_a_file_outside_the_folder_writes_nothing()
        {
            var data = SyntheticPack.Build();
            var cdn = new FakeCdn(data);
            var bad = new CytrusFragment("bad", new[] { data.Fragment.Files[0] with { Name = "../outside.bin" } }, data.Fragment.Bundles);

            await Assert.ThrowsAsync<SetupException>(
                () => ClientCdn.DownloadAsync(new HttpClient(cdn), new[] { bad }, _root, null, CancellationToken.None));

            Assert.Empty(cdn.Requests);
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "outside.bin")));
        }

        [Fact]
        public void The_texture_packs_are_not_part_of_the_client()
        {
            Assert.False(ClientCdn.IsPartOfTheClient("map_textures_2x"));
            Assert.False(ClientCdn.IsPartOfTheClient("map_textures_4x"));
            Assert.True(ClientCdn.IsPartOfTheClient("bones"));
            Assert.True(ClientCdn.IsPartOfTheClient("win32_x64"));
        }

        [Fact]
        public async Task A_version_the_cdn_does_not_serve_is_a_no_not_an_error()
        {
            var cdn = new FakeCdn(SyntheticPack.Build());

            Assert.False(await ClientCdn.IsServedAsync(new HttpClient(cdn), "3.6.10.11", CancellationToken.None));
            Assert.Contains(cdn.Others, url => url.EndsWith("/dofus/releases/dofus3/windows/6.0_3.6.10.11.manifest", StringComparison.Ordinal));
        }

        [Fact]
        public void The_room_asked_for_is_the_files_and_a_margin_not_an_archive_besides()
        {
            long size = 12_860_000_000;
            Assert.InRange(ClientCdn.SpaceNeeded(size), size, size + (2L << 30));
            Assert.True(ClientCdn.SpaceNeeded(size) < ClientDownload.SpaceNeeded(size));
        }

        private sealed class Inline : IProgress<ClientCdnProgress>
        {
            private readonly Action<ClientCdnProgress> _report;
            public Inline(Action<ClientCdnProgress> report) => _report = report;
            public void Report(ClientCdnProgress value) => _report(value);
        }
    }
}
