using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jondo.Unity.Launcher.Setup;
using Xunit;

namespace Jondo.Unity.Tests.Launcher
{
    /// <summary>
    /// The launcher's installer and updater: where updates come from, what an update fetches,
    /// how archives are unpacked, how the client is decrypted from MEGA and how MelonLoader's
    /// first run is followed.
    /// </summary>
    public class SetupTests
    {
        // ─── The repository and its releases ───────────────────────────────────

        [Theory]
        [InlineData("Keka-Bron/JondoEmu", "Keka-Bron/JondoEmu")]
        [InlineData("https://github.com/Keka-Bron/JondoEmu", "Keka-Bron/JondoEmu")]
        [InlineData("https://github.com/Keka-Bron/JondoEmu.git", "Keka-Bron/JondoEmu")]
        [InlineData("github.com/someone/fork-of-it/", "someone/fork-of-it")]
        [InlineData("  owner/name  ", "owner/name")]
        [InlineData("not a repo", null)]
        [InlineData("https://gitlab.com/a/b", null)]
        [InlineData("", null)]
        public void A_repository_is_read_as_owner_and_name(string text, string? expected)
            => Assert.Equal(expected, ReleaseFeed.Normalize(text));

        [Theory]
        [InlineData("v0.2.34", "v0.2.33", true)]
        [InlineData("v0.3.0", "v0.2.99", true)]
        [InlineData("v0.2.33", "v0.2.33", false)]
        [InlineData("v0.2.32", "v0.2.33", false)]
        [InlineData("v1.0.0", "", true)]
        public void A_release_is_newer_by_its_version(string candidate, string installed, bool newer)
            => Assert.Equal(newer, ReleaseFeed.IsNewer(candidate, installed));

        [Fact]
        public void The_client_link_is_found_in_the_readme_as_it_is_written()
        {
            string readme = System.IO.File.Exists(ReadmePath) ? System.IO.File.ReadAllText(ReadmePath)
                : "**⬇️ [Dofus 3.6.10.11 — download](https://mega.nz/file/EIcmDRaK#lRP1LPKjc1fBLzRHDNWieTxPWNeufdNhE-OYEUTj_f0)**";
            var link = ReleaseFeed.ClientLinkIn(readme);
            Assert.NotNull(link);
            Assert.Equal("3.6.10.11", link!.Value.Version);
            Assert.StartsWith("https://mega.nz/file/", link.Value.Url);
            Assert.NotNull(MegaLink.TryParse(link.Value.Url));
        }

        private static string ReadmePath
        {
            get
            {
                string? configured = Environment.GetEnvironmentVariable("JONDO_EMULATOR_ROOT");
                if (!string.IsNullOrEmpty(configured)) return Path.Combine(configured, "README.md");
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null && !System.IO.File.Exists(Path.Combine(dir.FullName, ".jondo-root"))) dir = dir.Parent;
                return dir == null ? "" : Path.Combine(dir.FullName, "README.md");
            }
        }

        [Fact]
        public void A_release_is_read_from_github_s_answer()
        {
            using var json = JsonDocument.Parse("""
                { "tag_name": "v0.3.0", "name": "v0.3.0", "body": "notes", "published_at": "2026-11-15T10:00:00Z",
                  "assets": [ { "name": "manifest-win-x64.json", "browser_download_url": "https://x/m.json", "size": 1234 },
                              { "name": "Jondo-Full-v0.3.0-win-x64.zip", "browser_download_url": "https://x/f.zip", "size": 99 } ] }
                """);
            var release = ReleaseFeed.Parse(json.RootElement);
            Assert.Equal("v0.3.0", release.Tag);
            Assert.Equal(1234, release.Asset("manifest-win-x64.json")!.Size);
            Assert.Null(release.Asset("nothing"));
        }

        [Fact]
        public void Raw_urls_escape_the_spaces_of_a_path()
        {
            var feed = new ReleaseFeed("owner/name", rawBase: "https://raw.example");
            Assert.Equal("https://raw.example/owner/name/v1/datos/a%20b.json", feed.RawUrl("v1", "datos/a b.json"));
        }

        // ─── The manifest and the update plan ──────────────────────────────────

        [Theory]
        [InlineData("datos/world.zip", true)]
        [InlineData("Jondo Server.exe", true)]
        [InlineData("../evil.dll", false)]
        [InlineData("datos/../../evil.dll", false)]
        [InlineData("/etc/passwd", false)]
        [InlineData("C:/Windows/x.dll", false)]
        [InlineData("datos//x", false)]
        [InlineData("", false)]
        public void Only_paths_inside_the_folder_are_allowed(string path, bool safe)
            => Assert.Equal(safe, ReleaseManifest.IsSafeRelativePath(path));

        [Fact]
        public void A_manifest_that_names_a_file_outside_the_folder_is_refused()
        {
            string json = """{ "version": "v1", "files": [ { "path": "../x.dll", "size": 1, "sha256": "%" } ] }"""
                .Replace("%", new string('a', 64));
            Assert.Throws<SetupException>(() => ReleaseManifest.Parse(json));
        }

        private static ManifestFile Entry(string path, long size, char hash, bool raw = true, string asset = "")
            => new() { Path = path, Size = size, Sha256 = new string(hash, 64), Raw = raw, Asset = asset };

        private static (string Root, InstallState State) Installed(params (string Path, string Content)[] files)
        {
            string root = Path.Combine(Path.GetTempPath(), "jondo-setup-test-" + Guid.NewGuid().ToString("N"));
            var state = new InstallState { Version = "v1" };
            foreach (var (path, content) in files)
            {
                string full = Path.Combine(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                System.IO.File.WriteAllText(full, content);
                state.Files[path] = UpdatePlan.HashOf(full);
            }
            return (root, state);
        }

        [Fact]
        public void An_update_fetches_only_what_changed_and_removes_what_is_gone()
        {
            var (root, state) = Installed(("datos/a.json", "aaa"), ("datos/b.json", "bbb"), ("datos/old.json", "old"));
            try
            {
                var release = new ReleaseManifest
                {
                    Files =
                    {
                        new ManifestFile { Path = "datos/a.json", Size = 3, Sha256 = state.Files["datos/a.json"], Raw = true },
                        Entry("datos/b.json", 4, 'c'),
                        Entry("datos/new.json", 10, 'd'),
                        Entry("Jondo Server.exe", 1000, 'e', raw: false, asset: "server-win-x64.exe"),
                    },
                };
                var plan = UpdatePlan.For(release, state, root, fullZipSize: 1_000_000);
                Assert.False(plan.Full);
                Assert.Equal(new[] { "datos/b.json", "datos/new.json", "Jondo Server.exe" }, plan.Fetch.Select(f => f.Path));
                Assert.Equal(new[] { "datos/old.json" }, plan.Remove);
                Assert.False(plan.ReplacesLauncher);
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void A_file_changed_by_hand_on_the_disk_is_fetched_again()
        {
            var (root, state) = Installed(("datos/a.json", "aaa"));
            try
            {
                System.IO.File.WriteAllText(Path.Combine(root, "datos/a.json"), "edited");
                var release = new ReleaseManifest
                {
                    Files = { new ManifestFile { Path = "datos/a.json", Size = 3, Sha256 = state.Files["datos/a.json"], Raw = true } },
                };
                Assert.Single(UpdatePlan.For(release, state, root, 1_000_000).Fetch);
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void Most_of_the_folder_changed_comes_in_the_full_zip()
        {
            var (root, state) = Installed(("datos/a.json", "aaa"));
            try
            {
                var release = new ReleaseManifest { Files = { Entry("datos/world.zip", 900, 'f') } };
                Assert.True(UpdatePlan.For(release, state, root, fullZipSize: 1000).Full);
                Assert.False(UpdatePlan.For(release, state, root, fullZipSize: 10_000).Full);

                // A file that cannot be had alone, and a first install, come in the zip too.
                var lone = new ReleaseManifest { Files = { Entry("x.bin", 1, 'a', raw: false) } };
                Assert.True(UpdatePlan.For(lone, state, root, 1_000_000).Full);
                Assert.True(UpdatePlan.For(release, new InstallState(), root, 1_000_000).Full);
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void The_launcher_among_the_files_means_it_starts_again()
        {
            var (root, state) = Installed(("datos/a.json", "aaa"));
            try
            {
                var release = new ReleaseManifest
                {
                    Files = { Entry(InstallLayout.LauncherFileName, 10, 'a', raw: false, asset: "launcher.exe") },
                };
                Assert.True(UpdatePlan.For(release, state, root, 1_000_000).ReplacesLauncher);
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void The_installation_s_state_survives_a_write_and_a_read()
        {
            string file = Path.Combine(Path.GetTempPath(), "jondo-state-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var state = new InstallState { Version = "v0.3.0", Repository = "a/b", Client = @"C:\x\Dofus.exe", Generated = true };
                state.Files["Datos/A.json"] = new string('a', 64);
                state.Write(file);
                var read = InstallState.Read(file);
                Assert.Equal("v0.3.0", read.Version);
                Assert.True(read.Generated);
                Assert.True(read.Files.ContainsKey("datos/a.json"));   // paths compare without case
            }
            finally { System.IO.File.Delete(file); }
        }

        // ─── Archives ──────────────────────────────────────────────────────────

        private static string Zip(params (string Name, string Content)[] entries)
        {
            string path = Path.Combine(Path.GetTempPath(), "jondo-zip-" + Guid.NewGuid().ToString("N") + ".zip");
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var (name, content) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
            return path;
        }

        [Fact]
        public void A_zip_with_one_folder_at_its_root_unpacks_without_it()
        {
            string zip = Zip(("JondoEmu/.jondo-root", ""), ("JondoEmu/datos/a.json", "a"), ("JondoEmu/Jondo Server.exe", "exe"));
            string into = Path.Combine(Path.GetTempPath(), "jondo-unzip-" + Guid.NewGuid().ToString("N"));
            try
            {
                Assert.Equal("JondoEmu", ArchiveExtractor.CommonRoot(zip));
                ArchiveExtractor.Extract(zip, into, ArchiveExtractor.CommonRoot(zip), null, default);
                Assert.Equal("a", System.IO.File.ReadAllText(Path.Combine(into, "datos", "a.json")));
                Assert.True(System.IO.File.Exists(Path.Combine(into, "Jondo Server.exe")));
            }
            finally { System.IO.File.Delete(zip); if (Directory.Exists(into)) Directory.Delete(into, true); }
        }

        [Fact]
        public void Only_the_files_asked_for_are_unpacked_from_a_full_zip()
        {
            string zip = Zip(("JondoEmu/datos/a.json", "a"), ("JondoEmu/datos/b.json", "b"));
            string into = Path.Combine(Path.GetTempPath(), "jondo-unzip-" + Guid.NewGuid().ToString("N"));
            try
            {
                ArchiveExtractor.Extract(zip, into, "JondoEmu", null, default,
                    onlyThese: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "datos/b.json" });
                Assert.False(System.IO.File.Exists(Path.Combine(into, "datos", "a.json")));
                Assert.True(System.IO.File.Exists(Path.Combine(into, "datos", "b.json")));
            }
            finally { System.IO.File.Delete(zip); if (Directory.Exists(into)) Directory.Delete(into, true); }
        }

        [Fact]
        public void A_zip_that_writes_outside_its_folder_is_refused()
        {
            string zip = Zip(("../escape.txt", "x"));
            string into = Path.Combine(Path.GetTempPath(), "jondo-unzip-" + Guid.NewGuid().ToString("N"));
            try
            {
                Assert.ThrowsAny<Exception>(() => ArchiveExtractor.Extract(zip, into, null, null, default));
                Assert.False(System.IO.File.Exists(Path.Combine(Path.GetDirectoryName(into)!, "escape.txt")));
            }
            finally { System.IO.File.Delete(zip); if (Directory.Exists(into)) Directory.Delete(into, true); }
        }

        // ─── MEGA ──────────────────────────────────────────────────────────────

        [Fact]
        public void Mega_links_old_and_new_are_read()
        {
            Assert.NotNull(MegaLink.TryParse("https://mega.nz/file/EIcmDRaK#lRP1LPKjc1fBLzRHDNWieTxPWNeufdNhE-OYEUTj_f0"));
            Assert.NotNull(MegaLink.TryParse("https://mega.nz/#!EIcmDRaK!lRP1LPKjc1fBLzRHDNWieTxPWNeufdNhE-OYEUTj_f0"));
            Assert.Null(MegaLink.TryParse("https://example.com/file.7z"));
            Assert.Equal("EIcmDRaK", MegaLink.TryParse("https://mega.nz/file/EIcmDRaK#lRP1LPKjc1fBLzRHDNWieTxPWNeufdNhE-OYEUTj_f0")!.Handle);
        }

        /// <summary>AES-CTR the long way: each block's counter encrypted on its own.</summary>
        private static byte[] ReferenceCtr(byte[] key, byte[] nonce, byte[] data)
        {
            using var aes = Aes.Create();
            aes.Key = key;
            var output = new byte[data.Length];
            for (int block = 0; block * 16 < data.Length; block++)
            {
                var counter = new byte[16];
                nonce.CopyTo(counter, 0);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(counter.AsSpan(8), (ulong)block);
                var stream = aes.EncryptEcb(counter, PaddingMode.None);
                for (int i = 0; i < 16 && block * 16 + i < data.Length; i++)
                    output[block * 16 + i] = (byte)(data[block * 16 + i] ^ stream[i]);
            }
            return output;
        }

        [Fact]
        public void The_decryptor_matches_aes_ctr_however_the_data_is_cut_and_from_where_it_resumes()
        {
            var random = new Random(7);
            var key = new byte[16]; random.NextBytes(key);
            var nonce = new byte[8]; random.NextBytes(nonce);
            var data = new byte[5000]; random.NextBytes(data);
            byte[] expected = ReferenceCtr(key, nonce, data);

            // In uneven pieces, as the network delivers them.
            var whole = (byte[])data.Clone();
            using (var ctr = new CtrDecryptor(key, nonce, 0))
                foreach (var (start, length) in new[] { (0, 7), (7, 100), (107, 1), (108, 4000), (4108, 892) })
                    ctr.Transform(whole.AsSpan(start, length));
            Assert.Equal(expected, whole);

            // Resumed from a block boundary, as after a cut download.
            var tail = data.Skip(2048).ToArray();
            using (var ctr = new CtrDecryptor(key, nonce, 2048)) ctr.Transform(tail);
            Assert.Equal(expected.Skip(2048).ToArray(), tail);
        }

        // ─── MelonLoader's first run ───────────────────────────────────────────

        [Fact]
        public void The_first_run_is_followed_stage_by_stage_from_the_log()
        {
            var lines = new List<string> { "[12:00:00.000] Loading Il2CppAssemblyGenerator..." };
            var (f1, s1, d1, _) = MelonLoaderSetup.Read(lines.ToArray(), TimeSpan.Zero);
            Assert.Equal("starting", s1);
            Assert.False(d1);

            lines.Add("[12:00:05.000] Checking GameAssembly...");
            lines.Add("[12:00:05.100] Assembly Generation Needed!");
            var (f2, s2, _, _) = MelonLoaderSetup.Read(lines.ToArray(), TimeSpan.FromSeconds(60));
            Assert.Equal("cpp2il", s2);
            Assert.True(f2 > f1);
            Assert.True(f2 < 0.56);   // within the stage, never past it

            lines.Add("[12:05:00.000] Generating Interop Assemblies...");
            var (f3, s3, _, _) = MelonLoaderSetup.Read(lines.ToArray(), TimeSpan.Zero);
            Assert.Equal("interop", s3);
            Assert.True(f3 >= 0.60);

            lines.Add("[12:09:00.000] 3 Mods loaded.");
            var (f4, _, d4, error) = MelonLoaderSetup.Read(lines.ToArray(), TimeSpan.Zero);
            Assert.True(d4);
            Assert.Equal(1, f4);
            Assert.Null(error);
        }

        [Fact]
        public void A_failed_generation_is_reported()
        {
            var (_, _, _, error) = MelonLoaderSetup.Read(new[]
            {
                "[12:00:00.000] Assembly Generation Needed!",
                "[12:03:00.000] [ERROR] Error Generating Interop Assemblies!",
            }, TimeSpan.Zero);
            Assert.NotNull(error);
        }

        // ─── The steps ─────────────────────────────────────────────────────────

        [Fact]
        public void A_step_s_bar_never_goes_back()
        {
            var step = new SetupStep("x", "X", 1, TimeSpan.FromSeconds(10));
            step.Start();
            step.Report(0.5);
            step.Report(0.3);
            Assert.Equal(0.5, step.Fraction);
            step.Finish();
            Assert.Equal(1, step.Fraction);
            Assert.Equal(StepState.Done, step.State);
            Assert.Equal(TimeSpan.Zero, step.Remaining);
        }

        [Fact]
        public void The_time_left_follows_the_pace_measured()
        {
            var eta = new EtaEstimator(TimeSpan.FromMinutes(10));
            // Before any measure: what is expected.
            Assert.Equal(TimeSpan.FromMinutes(10), eta.Remaining(0, TimeSpan.Zero));
            // Ten percent in ten seconds, steadily: about ninety seconds left.
            TimeSpan? left = null;
            for (int s = 1; s <= 10; s++) left = eta.Remaining(s / 100.0, TimeSpan.FromSeconds(s));
            Assert.NotNull(left);
            Assert.InRange(left!.Value.TotalSeconds, 60, 120);
        }

        [Theory]
        [InlineData("", true)]
        [InlineData("127.0.0.1", true)]
        [InlineData("localhost", true)]
        [InlineData("203.0.113.7", false)]
        [InlineData("play.example.org", false)]
        public void The_server_is_started_by_the_setup_only_when_it_is_this_machine_s(string host, bool started)
        {
            var steps = new Installer(new InstallOptions { Root = Path.GetTempPath(), ServerHost = host }, Jondo.Unity.Launcher.UI.Language.Es).Steps
                .Select(s => s.Key).ToList();

            Assert.Equal(started, steps.Contains("server"));
            // Always the last real step: the launcher that opens next finds it answering.
            Assert.Equal("finish", steps[^1]);
            if (started) Assert.Equal("server", steps[^2]);
        }
    }
}
