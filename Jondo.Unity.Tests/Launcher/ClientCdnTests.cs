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

            Assert.False(await ClientCdn.IsServedAsync(new HttpClient(cdn), "3.6.10.11", ClientCdn.Windows, CancellationToken.None));
            Assert.Contains(cdn.Others, url => url.EndsWith("/dofus/releases/dofus3/windows/6.0_3.6.10.11.manifest", StringComparison.Ordinal));
        }

        [Fact]
        public async Task The_linux_client_is_asked_for_by_its_own_manifest()
        {
            var cdn = new FakeCdn(SyntheticPack.Build());

            await ClientCdn.IsServedAsync(new HttpClient(cdn), "3.6.10.11", ClientCdn.Linux, CancellationToken.None);

            Assert.Contains(cdn.Others, url => url.EndsWith("/dofus/releases/dofus3/linux/6.0_3.6.10.11.manifest", StringComparison.Ordinal));
        }

        [Fact]
        public void Each_platform_has_its_executable_and_a_folder_of_its_own()
        {
            Assert.Equal("Dofus.exe", ClientCdn.ExecutableFor(ClientCdn.Windows));
            Assert.Equal("Dofus.x64", ClientCdn.ExecutableFor(ClientCdn.Linux));
            Assert.Equal(ClientCdn.Windows, ClientCdn.PlatformOf(@"C:\Games\Dofus.exe"));
            Assert.Equal(ClientCdn.Linux, ClientCdn.PlatformOf("/home/player/Dofus/Dofus.x64"));
            // Their executable parts write different files under the same names in Dofus_Data.
            Assert.Equal("Cliente 3.6.10.11", ClientCdn.FolderName("3.6.10.11", ClientCdn.Windows));
            Assert.Equal("Cliente 3.6.10.11 Linux", ClientCdn.FolderName("3.6.10.11", ClientCdn.Linux));
            Assert.NotEqual(ClientCdn.FolderName("3.6.10.11", ClientCdn.Windows), ClientCdn.FolderName("3.6.10.11", ClientCdn.Linux));
        }

        [Fact]
        public void Only_a_client_this_machine_can_start_is_playable_here()
        {
            Assert.True(ClientCdn.RunsHere(ClientCdn.HostPlatform));
            // The Windows client runs everywhere (through Wine off Windows); the Linux one only on Linux.
            Assert.True(ClientCdn.RunsHere(ClientCdn.Windows));
            Assert.Equal(!OperatingSystem.IsWindows(), ClientCdn.RunsHere(ClientCdn.Linux));
        }

        [Fact]
        public void The_start_script_of_a_linux_client_stands_for_the_executable_beside_it()
        {
            File.WriteAllText(Path.Combine(_root, ClientCdn.StartScript), "#!/bin/sh");
            Assert.Equal(Path.Combine(_root, ClientCdn.StartScript), ClientCdn.ExecutableChosen(Path.Combine(_root, ClientCdn.StartScript)));

            File.WriteAllText(Path.Combine(_root, "Dofus.x64"), "");
            Assert.Equal(Path.Combine(_root, "Dofus.x64"), ClientCdn.ExecutableChosen(Path.Combine(_root, ClientCdn.StartScript)));
            Assert.Equal(Path.Combine(_root, "Dofus.exe"), ClientCdn.ExecutableChosen(Path.Combine(_root, "Dofus.exe")));
            Assert.Equal(ClientCdn.Linux, ClientCdn.PlatformIn(_root));
        }

        [Fact]
        public async Task A_folder_holding_the_other_platforms_client_is_refused_before_anything_is_fetched()
        {
            var cdn = new FakeCdn(SyntheticPack.Build());
            File.WriteAllText(Path.Combine(_root, "Dofus.exe"), "");

            var error = await Assert.ThrowsAsync<SetupException>(() => ClientCdn.FetchAsync(
                new HttpClient(cdn), "3.6.10.11", ClientCdn.Linux, _root, null, null, CancellationToken.None));

            Assert.Contains("windows", error.Message);
            Assert.Empty(cdn.Requests);
            Assert.Empty(cdn.Others);
        }

        [Fact]
        public async Task Files_the_manifest_marks_executable_can_be_run_after_the_download()
        {
            if (OperatingSystem.IsWindows()) return; // NTFS has no execute bit to set.
            var data = SyntheticPack.Build();
            var cdn = new FakeCdn(data);
            var files = data.Fragment.Files.Select((f, n) => n == 0 ? f with { Executable = true } : f).ToArray();
            var fragment = data.Fragment with { Files = files };

            await ClientCdn.DownloadAsync(new HttpClient(cdn), new[] { fragment }, _root, null, CancellationToken.None);

            var mode = File.GetUnixFileMode(Local(files[0].Name));
            Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
            Assert.False(File.GetUnixFileMode(Local(files[1].Name)).HasFlag(UnixFileMode.UserExecute));
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
