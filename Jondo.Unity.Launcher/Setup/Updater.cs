using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jondo.Unity.Launcher.Setup
{
    /// <summary>What a look at the repository found: a newer release and what updating to it takes.</summary>
    internal sealed record UpdateCheck(Release Release, ReleaseManifest Manifest, UpdatePlan Plan, ReleaseAsset? FullZip)
    {
        /// <summary>The bytes the update downloads: the files one by one, or the whole zip.</summary>
        public long DownloadSize => Plan.Full ? FullZip?.Size ?? 0 : Plan.BytesToFetch;
    }

    /// <summary>
    /// An update of an installation to a newer release: the files that changed and nothing else,
    /// fetched one by one from the repository (the data, as the tag has them) and the release
    /// (the executables), or the whole zip when most of it changed. The databases, the settings
    /// and the logs are never touched: only the files the release names.
    /// </summary>
    /// <remarks>
    /// The local server is stopped first -- its files are among those replaced, and its database
    /// must not be written halfway. The launcher replaces itself like any other file: Windows lets
    /// a running executable be renamed, so it goes aside as <c>.old</c>, the new one takes its
    /// place, and the launcher starts again from it.
    /// </remarks>
    internal sealed class Updater
    {
        private readonly InstallLayout _layout;
        private readonly UpdateCheck _check;
        private readonly UI.Language _lang;

        public Updater(InstallLayout layout, UpdateCheck check, UI.Language language)
        {
            _layout = layout;
            _check = check;
            _lang = language;
            Steps = new List<SetupStep>
            {
                new("stop-server", UI.SetupTexts.StepStopServer(language), 1, TimeSpan.FromSeconds(10)),
                new("download", UI.SetupTexts.StepDownloadUpdate(language), 10,
                    TimeSpan.FromSeconds(Math.Max(10, check.DownloadSize / (4.0 * 1024 * 1024)))),
                new("apply", UI.SetupTexts.StepApplyUpdate(language), 3, TimeSpan.FromSeconds(check.Plan.Full ? 40 : 5)),
                new("mods", UI.SetupTexts.StepMods(language), 1, TimeSpan.FromSeconds(2)),
                new("finish", UI.SetupTexts.StepFinish(language), 1, TimeSpan.FromSeconds(2)),
            };
        }

        public List<SetupStep> Steps { get; }

        public SetupStep Step(string key) => Steps.First(s => s.Key == key);

        public double Fraction
        {
            get
            {
                double total = Steps.Sum(s => s.Weight);
                return total <= 0 ? 0 : Steps.Sum(s => s.Weight * s.Fraction) / total;
            }
        }

        public TimeSpan? Remaining
        {
            get
            {
                TimeSpan sum = TimeSpan.Zero;
                foreach (var step in Steps)
                {
                    if (step.State is StepState.Done or StepState.Skipped) continue;
                    if (step.Remaining is not { } left) return null;
                    sum += left;
                }
                return sum;
            }
        }

        /// <summary>Whether the launcher was among the files replaced and has to start again.</summary>
        public bool LauncherReplaced { get; private set; }

        /// <summary>
        /// Looks for a release newer than the one installed. Null when there is none; throws a
        /// <see cref="SetupException"/> when the repository cannot be read.
        /// </summary>
        public static async Task<UpdateCheck?> CheckAsync(InstallLayout layout, InstallState state, CancellationToken cancel)
        {
            var feed = new ReleaseFeed(state.Repository.Length > 0 ? state.Repository : ReleaseFeed.DefaultRepository);
            var release = await feed.LatestAsync(cancel);
            if (!ReleaseFeed.IsNewer(release.Tag, state.Version)) return null;
            string platform = state.Platform.Length > 0 ? state.Platform : Installer.Platform;
            var manifestAsset = release.Asset($"manifest-{platform}.json")
                                ?? throw new SetupException($"Release {release.Tag} has no manifest for {platform}.");
            var manifest = ReleaseManifest.Parse(await Http.Shared.GetStringAsync(manifestAsset.Url, cancel));
            var full = release.Asset(manifest.Full);
            var plan = UpdatePlan.For(manifest, state, layout.Root, full?.Size ?? 0);
            return new UpdateCheck(release, manifest, plan, full);
        }

        public async Task RunAsync(CancellationToken cancel)
        {
            var state = _layout.Load();
            var plan = _check.Plan;
            var release = _check.Release;
            var feed = new ReleaseFeed(state.Repository.Length > 0 ? state.Repository : ReleaseFeed.DefaultRepository);
            string staging = Path.Combine(_layout.DownloadsDirectory, release.Tag);
            Directory.CreateDirectory(staging);
            Console.WriteLine($"[Update] {state.Version} -> {release.Tag}: {plan.Fetch.Count} file(s) to fetch, " +
                              $"{plan.Remove.Count} to remove{(plan.Full ? ", through the full zip" : "")}.");

            // 1. The local server, closed by its own hand so its database is left whole.
            var step = Step("stop-server");
            step.Start();
            int stopped = await Guard(step, () => StopServerAsync(_layout, cancel));
            if (stopped == 0) step.Skip(); else step.Finish();

            // 2. The files, into the staging folder.
            step = Step("download");
            step.Start();
            string? zip = null;
            if (plan.Full)
            {
                var full = _check.FullZip ?? throw Fail(step, $"Release {release.Tag} has no {_check.Manifest.Full}.");
                zip = Path.Combine(staging, full.Name);
                await Guard(step, () => Downloader.DownloadAsync(full.Url, zip, full.Size, null, Bytes(step), cancel));
            }
            else
            {
                long total = Math.Max(1, plan.BytesToFetch), before = 0;
                foreach (var file in plan.Fetch)
                {
                    string url = file.Asset.Length > 0
                        ? (release.Asset(file.Asset) ?? throw Fail(step, $"Release {release.Tag} has no {file.Asset}.")).Url
                        : feed.RawUrl(release.Tag, file.Path);
                    string target = Path.Combine(staging, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    long offset = before;
                    var each = new Progress<Transfer>(t => step.Report((offset + t.Done) / (double)total,
                        $"{file.Path} · {Format.Bytes(offset + t.Done)} / {Format.Bytes(total)}" +
                        (t.BytesPerSecond > 0 ? $" · {Format.Speed(t.BytesPerSecond)}" : "")));
                    await Guard(step, () => Downloader.DownloadAsync(url, target, file.Size, file.Sha256, each, cancel));
                    before += file.Size;
                }
            }
            step.Finish(Format.Bytes(_check.DownloadSize));

            // 3. Into place: each file over the old one (the running launcher aside as .old).
            step = Step("apply");
            step.Start();
            await Guard(step, () => Task.Run(() =>
            {
                var wanted = new HashSet<string>(plan.Fetch.Select(f => f.Path.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
                if (zip != null)
                {
                    ArchiveExtractor.Extract(zip, _layout.Root, ArchiveExtractor.CommonRoot(zip), Bytes(step), cancel,
                        onlyThese: wanted, current: f => step.Say(f));
                }
                else
                {
                    int done = 0;
                    foreach (var file in plan.Fetch)
                    {
                        cancel.ThrowIfCancellationRequested();
                        string from = Path.Combine(staging, file.Path);
                        string to = Path.Combine(_layout.Root, file.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                        step.Report(done / (double)plan.Fetch.Count, file.Path);
                        File.Move(from, to + ".jondo-new", overwrite: true);
                        ArchiveExtractor.ReplaceFile(to + ".jondo-new", to);
                        done++;
                    }
                }
                foreach (string gone in plan.Remove)
                {
                    string path = Path.Combine(_layout.Root, gone);
                    try { if (File.Exists(path)) File.Delete(path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Console.WriteLine($"[Update] {gone} could not be removed: {ex.Message}");
                    }
                }

                _layout.MarkExecutables();

                // What is installed now, by the hashes the release gives.
                foreach (var file in plan.Fetch) state.Files[file.Path] = file.Sha256.ToLowerInvariant();
                foreach (string gone in plan.Remove) state.Files.Remove(gone);
            }, cancel));
            LauncherReplaced = plan.ReplacesLauncher;
            state.Version = release.Tag;
            state.UpdatedAt = DateTime.Now;
            _layout.Save(state);
            if (OperatingSystem.IsWindows()) SystemIntegration.UpdateRegisteredVersion(_layout.Root, release.Tag);
            step.Finish(UI.SetupTexts.UpdateSize(_lang, plan.Fetch.Count, Format.Bytes(_check.DownloadSize), plan.Full));

            // 4. The mods, which may have changed with it, into the client.
            step = Step("mods");
            string client = state.Client.Length > 0 ? Path.GetDirectoryName(state.Client) ?? "" : "";
            if (!OperatingSystem.IsWindows() || client.Length == 0 || !Directory.Exists(client)) step.Skip();
            else
            {
                step.Start();
                var retiredMods = plan.Remove.Select(path => path.Replace('\\', '/').Split('/'))
                    .Where(parts => parts.Length == 2 && parts[0].Equals("mods", StringComparison.OrdinalIgnoreCase)
                        && ClientModManifest.IsDllName(parts[1]))
                    .Select(parts => parts[1]).ToArray();
                if (Directory.Exists(Path.Combine(client, "Mods")))
                {
                    foreach (string name in retiredMods) BundledClientMods.Remember(client, name);
                    BundledClientMods.SetAsideLegacyCopies(client, retiredMods);
                }
                int copied = MelonLoaderSetup.InstallMods(_layout.Root, client);
                step.Finish(copied > 0 ? string.Join(", ", BundledClientMods.Discover(_layout.Root).Keys) : UI.SetupTexts.AlreadyThere(_lang));
            }

            // 5. The staging folder goes.
            step = Step("finish");
            step.Start();
            TryDeleteDirectory(staging);
            Console.WriteLine($"[Update] Now at {release.Tag}.");
            step.Finish(release.Tag);
        }

        /// <summary>
        /// Closes the servers running from this installation and waits for them to end; how many
        /// there were. A server that does not close in a minute is ended.
        /// </summary>
        internal static async Task<int> StopServerAsync(InstallLayout layout, CancellationToken cancel)
        {
            var running = ServersOf(layout.ServerExecutable);
            if (running.Count == 0) return 0;

            if (OperatingSystem.IsWindows()) Contract.RequestStop("JondoEmuServidor");
            else
                foreach (var server in running)
                    try { Process.Start("kill", $"-TERM {server.Id}")?.WaitForExit(5000); } catch (Exception) { }

            var deadline = DateTime.Now.AddMinutes(1);
            foreach (var server in running)
            {
                using (server)
                {
                    while (!server.HasExited && DateTime.Now < deadline) await Task.Delay(300, cancel);
                    if (!server.HasExited)
                    {
                        Console.WriteLine($"[Update] The server (PID {server.Id}) did not close; it is ended.");
                        try { server.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                        await server.WaitForExitAsync(cancel);
                    }
                }
            }
            return running.Count;
        }

        /// <summary>The processes running that executable.</summary>
        internal static List<Process> ServersOf(string executable)
        {
            var found = new List<Process>();
            string name = Path.GetFileNameWithoutExtension(executable);
            foreach (var process in Process.GetProcessesByName(name))
            {
                string? path = null;
                try { path = process.MainModule?.FileName; } catch (Exception) { }
                if (path != null && Path.GetFullPath(path).Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase))
                    found.Add(process);
                else
                    process.Dispose();
            }
            return found;
        }

        private static IProgress<Transfer> Bytes(SetupStep step)
            => new Progress<Transfer>(t => step.Report(t.Fraction,
                $"{Format.Bytes(t.Done)} / {Format.Bytes(t.Total)}" + (t.BytesPerSecond > 0 ? $" · {Format.Speed(t.BytesPerSecond)}" : "")));

        private static SetupException Fail(SetupStep step, string why)
        {
            step.Fail(why);
            return new SetupException(why);
        }

        private static async Task<T> Guard<T>(SetupStep step, Func<Task<T>> work)
        {
            try { return await work(); }
            catch (OperationCanceledException) { step.Fail("—"); throw; }
            catch (SetupException ex) { step.Fail(ex.Message); throw; }
            catch (Exception ex) { step.Fail(ex.Message); throw new SetupException(ex.Message, ex); }
        }

        private static async Task Guard(SetupStep step, Func<Task> work)
            => await Guard<bool>(step, async () => { await work(); return true; });

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
