using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Setup;
using Xunit;
using Xunit.Abstractions;

namespace Jondo.Unity.Tests.Launcher
{
    /// <summary>
    /// The update a player's launcher makes when a release comes out, run for real against GitHub:
    /// an installation of an older release, unpacked from that release's own zip, is brought up to
    /// the latest one and then holds exactly the files the latest manifest names.
    /// </summary>
    /// <remarks>
    /// Downloads a few hundred megabytes, so it runs only when JONDO_LIVE_UPDATE_ROOT names an empty
    /// (or earlier) test folder; JONDO_LIVE_UPDATE_FROM picks the release to start from. The
    /// installation it makes has no client, so the mods step is skipped, and the uninstall entry of
    /// a real installation elsewhere is left as it is.
    /// </remarks>
    public class LiveUpdateTests
    {
        private readonly ITestOutputHelper _out;

        public LiveUpdateTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public async Task An_older_installation_updates_to_the_latest_release()
        {
            string? root = Environment.GetEnvironmentVariable("JONDO_LIVE_UPDATE_ROOT");
            string? from = Environment.GetEnvironmentVariable("JONDO_LIVE_UPDATE_FROM");
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(from)) return;

            const string platform = "win-x64";
            var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(30)).Token;
            var feed = new ReleaseFeed(ReleaseFeed.DefaultRepository);
            var layout = new InstallLayout(root);

            // The older release, as its installer would have left it.
            if (Directory.Exists(layout.Root)) Directory.Delete(layout.Root, recursive: true);
            Directory.CreateDirectory(layout.Root);
            Release older;
            using (var json = System.Text.Json.JsonDocument.Parse(await Http.Shared.GetStringAsync(
                       $"{feed.ApiBase}/repos/{feed.Repository}/releases/tags/{Uri.EscapeDataString(from)}", cancel)))
                older = ReleaseFeed.Parse(json.RootElement);
            var olderManifest = ReleaseManifest.Parse(
                await Http.Shared.GetStringAsync(older.Asset($"manifest-{platform}.json")!.Url, cancel));
            var olderZip = older.Asset(olderManifest.Full)!;
            string zip = Path.Combine(Path.GetTempPath(), olderZip.Name);
            if (!File.Exists(zip) || new FileInfo(zip).Length != olderZip.Size)
                await Downloader.DownloadAsync(olderZip.Url, zip, olderZip.Size, null, null, cancel);
            ArchiveExtractor.Extract(zip, layout.Root, ArchiveExtractor.CommonRoot(zip), null, cancel);
            var state = new InstallState
            {
                Version = older.Tag,
                Repository = ReleaseFeed.DefaultRepository,
                Platform = platform,
            };
            foreach (var file in olderManifest.Files) state.Files[file.Path] = file.Sha256.ToLowerInvariant();
            layout.Save(state);

            // The launcher's start: a newer release, and the update to it.
            var check = await Updater.CheckAsync(layout, layout.Load(), cancel);
            Assert.NotNull(check);
            _out.WriteLine($"{older.Tag} -> {check!.Release.Tag}: {check.Plan.Fetch.Count} file(s), " +
                           $"{check.Plan.Remove.Count} removed, full zip: {check.Plan.Full}, {check.DownloadSize} bytes");
            foreach (var file in check.Plan.Fetch.Take(40)) _out.WriteLine("  " + file.Path);

            var updater = new Updater(layout, check, Jondo.Unity.Launcher.UI.Language.En);
            await updater.RunAsync(cancel);

            Assert.All(updater.Steps, s => Assert.True(s.State is StepState.Done or StepState.Skipped, $"{s.Key}: {s.State}"));
            var after = layout.Load();
            Assert.Equal(check.Release.Tag, after.Version);
            Assert.Null(await Updater.CheckAsync(layout, after, cancel));
            foreach (var file in check.Manifest.Files)
            {
                string local = Path.Combine(layout.Root, file.Path);
                Assert.True(File.Exists(local), file.Path + " is missing");
                Assert.Equal(file.Sha256.ToLowerInvariant(), UpdatePlan.HashOf(local));
            }
            Assert.All(check.Plan.Remove, gone => Assert.False(File.Exists(Path.Combine(layout.Root, gone)), gone));
        }
    }
}
