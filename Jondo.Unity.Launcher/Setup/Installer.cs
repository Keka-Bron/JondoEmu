using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jondo.Unity.Launcher.Setup
{
    /// <summary>What the player chose in the installer's pages.</summary>
    internal sealed class InstallOptions
    {
        public string Root { get; set; } = InstallLayout.DefaultRoot;
        public string Repository { get; set; } = ReleaseFeed.DefaultRepository;

        /// <summary>The client the player already has; empty to download it.</summary>
        public string ExistingClient { get; set; } = "";

        /// <summary>
        /// Which client to download when there is none: <see cref="ClientCdn.Windows"/> or
        /// <see cref="ClientCdn.Linux"/>. Only one this machine can play makes sense here; on
        /// Linux that is either.
        /// </summary>
        public string ClientPlatform { get; set; } = ClientCdn.HostPlatform;

        /// <summary>The server's address; empty or 127.0.0.1 for this machine.</summary>
        public string ServerHost { get; set; } = "";

        public bool Shortcuts { get; set; } = true;
    }

    /// <summary>
    /// A first install, step by step: the release, .NET, the folder, the emulator's files, the
    /// client, MelonLoader with the mods and its first run, the database. Each step reports to
    /// its <see cref="SetupStep"/>, which the window draws; a step that fails stops the rest and
    /// says why, and running it again picks up what was left (every download resumes, every
    /// finished step is recognised and skipped).
    /// </summary>
    internal sealed class Installer
    {
        private readonly InstallOptions _options;
        private readonly UI.Language _lang;
        private readonly Func<SystemIntegration.Request, bool> _privileged;

        public Installer(InstallOptions options, UI.Language language, Func<SystemIntegration.Request, bool>? privileged = null)
        {
            _options = options;
            _lang = language;
            _privileged = privileged ?? SystemIntegration.RunPrivileged;
            bool windows = OperatingSystem.IsWindows();
            bool download = options.ExistingClient.Length == 0;

            Steps = new List<SetupStep>
            {
                new("release", UI.SetupTexts.StepRelease(language), 1, TimeSpan.FromSeconds(5)),
                new("dotnet", UI.SetupTexts.StepDotNet(language), 4, TimeSpan.FromSeconds(60)),
                new("system", UI.SetupTexts.StepSystem(language), 2, TimeSpan.FromSeconds(30)),
                new("emulator", UI.SetupTexts.StepEmulator(language), 10, TimeSpan.FromMinutes(2)),
                new("files", UI.SetupTexts.StepFiles(language), 4, TimeSpan.FromSeconds(40)),
            };
            if (download)
            {
                Steps.Add(new("client-download", UI.SetupTexts.StepClientDownload(language), 50, TimeSpan.FromMinutes(40)));
            }
            else
            {
                Steps.Add(new("client", UI.SetupTexts.StepClient(language), 1, TimeSpan.FromSeconds(2)));
            }
            if (windows)
            {
                Steps.Add(new("melonloader", UI.SetupTexts.StepMelonLoader(language), 3, TimeSpan.FromSeconds(30)));
                Steps.Add(new("mods", UI.SetupTexts.StepMods(language), 1, TimeSpan.FromSeconds(2)));
                Steps.Add(new("generation", UI.SetupTexts.StepGeneration(language), 25, TimeSpan.FromMinutes(8)));
            }
            Steps.Add(new("database", UI.SetupTexts.StepDatabase(language), 3, TimeSpan.FromSeconds(30)));
            if (UI.LauncherPreferences.IsThisMachine(options.ServerHost))
                Steps.Add(new("server", UI.SetupTexts.StepServer(language), 3, TimeSpan.FromSeconds(40)));
            Steps.Add(new("finish", UI.SetupTexts.StepFinish(language), 1, TimeSpan.FromSeconds(2)));
        }

        public List<SetupStep> Steps { get; }

        public SetupStep Step(string key) => Steps.First(s => s.Key == key);

        /// <summary>The whole bar: the steps' fractions by their weights.</summary>
        public double Fraction
        {
            get
            {
                double total = Steps.Sum(s => s.Weight);
                return total <= 0 ? 0 : Steps.Sum(s => s.Weight * s.Fraction) / total;
            }
        }

        /// <summary>The time the whole has left: the running step's measure and the expected of those to come.</summary>
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

        public InstallLayout? Layout { get; private set; }

        /// <summary>Set while the client's download (the README's link, when the CDN fails) waits on MEGA's quota: until when.</summary>
        public DateTime? QuotaUntil { get; private set; }

        public async Task RunAsync(CancellationToken cancel)
        {
            var layout = Layout = new InstallLayout(_options.Root);
            string downloads = Path.Combine(Path.GetTempPath(), "JondoEmu-setup");
            Directory.CreateDirectory(downloads);
            var feed = new ReleaseFeed(_options.Repository);
            string platform = Platform;

            // 1. The release and its manifest.
            var step = Step("release");
            step.Start(_options.Repository);
            var release = await Guard(step, () => feed.LatestAsync(cancel));
            var manifestAsset = release.Asset($"manifest-{platform}.json")
                                ?? throw Fail(step, UI.SetupTexts.NoManifest(_lang, release.Tag, platform));
            var manifest = ReleaseManifest.Parse(await Http.Shared.GetStringAsync(manifestAsset.Url, cancel));
            var full = release.Asset(manifest.Full) ?? throw Fail(step, UI.SetupTexts.NoManifest(_lang, release.Tag, platform));
            step.Finish($"{release.Tag} · {Format.Bytes(full.Size)}");

            // 2. .NET, downloaded here and installed with the folder.
            step = Step("dotnet");
            string dotnetInstaller = "";
            if (DotNetRuntime.IsInstalled()) step.Skip(UI.SetupTexts.AlreadyThere(_lang));
            else
            {
                step.Start();
                dotnetInstaller = await Guard(step, () => DotNetRuntime.DownloadAsync(downloads, Bytes(step), cancel));
                step.Finish();
            }

            // 3. The folder (an administrator, once, when it is Program Files), the shortcuts, the entry.
            step = Step("system");
            step.Start(layout.Root);
            var request = new SystemIntegration.Request
            {
                Root = layout.Root, DotNetInstaller = dotnetInstaller, Version = release.Tag, Shortcuts = _options.Shortcuts,
                UserSid = OperatingSystem.IsWindows() ? SystemIntegration.CurrentUserSid() : "",
            };
            bool allowed = await Guard(step, () => Task.Run(() => _privileged(request), cancel));
            if (!allowed) throw Fail(step, UI.SetupTexts.ElevationRefused(_lang));
            step.Finish();

            // 4. The emulator's zip.
            step = Step("emulator");
            step.Start();
            string zip = Path.Combine(downloads, full.Name);
            await Guard(step, () => Downloader.DownloadAsync(full.Url, zip, full.Size, null, Bytes(step), cancel));
            step.Finish(Format.Bytes(full.Size));

            // 5. Its files into the folder, and the state that names them.
            step = Step("files");
            step.Start();
            var state = layout.Load();
            await Guard(step, () => Task.Run(() =>
            {
                ArchiveExtractor.Extract(zip, layout.Root, ArchiveExtractor.CommonRoot(zip), Bytes(step), cancel, current: f => step.Say(f));
                layout.MarkExecutables();
                state.Files.Clear();
                foreach (var file in manifest.Files)
                {
                    string local = Path.Combine(layout.Root, file.Path);
                    if (File.Exists(local)) state.Files[file.Path] = UpdatePlan.HashOf(local);
                }
            }, cancel));
            state.Version = release.Tag;
            state.Repository = _options.Repository;
            state.Platform = platform;
            if (state.InstalledAt == default) state.InstalledAt = DateTime.Now;
            state.UpdatedAt = DateTime.Now;
            layout.Save(state);
            TryDelete(zip);
            step.Finish($"{state.Files.Count} {UI.SetupTexts.Files(_lang)}");

            // 6. The client: the one the player has, or Ankama's from its CDN (the README's link
            //    when the CDN no longer serves that version).
            string client;
            if (_options.ExistingClient.Length > 0)
            {
                step = Step("client");
                step.Start(_options.ExistingClient);
                client = CheckClient(step, _options.ExistingClient);
                step.Finish(Path.GetDirectoryName(client) ?? "");
            }
            else
            {
                step = Step("client-download");
                step.Start();
                var dlStep = step;
                // The version the README names, so a new client is a change to the README; the
                // emulator's own when the README cannot be read or links none.
                var link = await ReadmeLinkAsync(feed, release.Tag, cancel);
                string version = link?.Version ?? ClientDownload.Version;
                string clientPlatform = ClientCdn.RunsHere(_options.ClientPlatform) ? _options.ClientPlatform : ClientCdn.HostPlatform;
                string folder = Path.Combine(layout.Root, ClientCdn.FolderName(version, clientPlatform));
                // The chosen platform's own executable: a folder can hold both clients, and
                // looking for "a" client would take whichever this system prefers.
                string executable = Path.Combine(folder, ClientCdn.ExecutableFor(clientPlatform));

                if (state.ClientDownloaded && File.Exists(executable)
                    && Packs.ClientVersion.Read(folder) == version
                    && !Directory.Exists(Path.Combine(folder, ClientCdn.WorkFolder)))
                {
                    client = executable;
                    step.Skip(UI.SetupTexts.AlreadyThere(_lang));
                }
                else
                {
                    if (await Guard(step, () => ClientCdn.IsServedAsync(Http.Shared, version, clientPlatform, cancel)))
                    {
                        step.Say(UI.SetupTexts.FromAnkama(_lang, version, clientPlatform));
                        var reading = new Progress<Packs.PackProgress>(p => dlStep.Report(0, UI.SetupTexts.ReadingFileList(_lang, p.Fraction)));
                        var fetching = FromCdn(dlStep);
                        client = await Guard(step, () => Task.Run(
                            () => ClientCdn.FetchAsync(Http.Shared, version, clientPlatform, folder, reading, fetching, cancel), cancel));
                    }
                    else
                    {
                        // Not on the CDN any more: the README's link, an archive to download and
                        // unpack. It is the Windows client; a Linux one has nowhere else to come from.
                        if (clientPlatform != ClientCdn.Windows)
                            throw Fail(step, UI.SetupTexts.ClientNotOnCdn(_lang, version, clientPlatform));
                        if (link is not { } l) throw Fail(step, UI.SetupTexts.NoClientLink(_lang));
                        step.Say(l.Url);
                        string archive = await Guard(step, () => ClientDownload.DownloadAsync(l.Url, downloads, Scaled(dlStep, 0, 0.75),
                            until => { QuotaUntil = until; if (until is { } u) dlStep.Say(UI.SetupTexts.QuotaWait(_lang, u)); }, cancel));
                        QuotaUntil = null;
                        client = await Guard(step, () => Task.Run(() => ClientDownload.Unpack(archive, layout.Root, Scaled(dlStep, 0.75, 1),
                            f => dlStep.Say(f), cancel), cancel));
                        TryDelete(archive);
                    }
                    state.ClientDownloaded = true;
                    step.Finish(Path.GetDirectoryName(client) ?? "");
                }
            }
            state.Client = client;
            layout.Save(state);
            UI.LauncherPreferences.SetClientExecutable(client);
            UI.LauncherPreferences.ServerHost = _options.ServerHost;

            // 7-9. MelonLoader, the mods and its first run, on Windows.
            if (OperatingSystem.IsWindows())
            {
                string clientRoot = Path.GetDirectoryName(client)!;
                step = Step("melonloader");
                if (MelonLoaderSetup.IsInstalled(clientRoot) && state.MelonLoader == MelonLoaderSetup.Version)
                    step.Skip(UI.SetupTexts.AlreadyThere(_lang));
                else
                {
                    step.Start();
                    var mlStep = step;
                    await Guard(step, async () =>
                    {
                        await MelonLoaderSetup.DownloadAsync(downloads, Scaled(mlStep, 0, 0.8), cancel);
                        await Task.Run(() => MelonLoaderSetup.Unpack(downloads, clientRoot, Scaled(mlStep, 0.8, 1), cancel), cancel);
                    });
                    state.MelonLoader = MelonLoaderSetup.Version;
                    state.Generated = false;
                    layout.Save(state);
                    step.Finish($"MelonLoader {MelonLoaderSetup.Version}");
                }

                step = Step("mods");
                step.Start();
                int copied = MelonLoaderSetup.InstallMods(layout.Root, clientRoot);
                step.Finish(string.Join(", ", BundledClientMods.Discover(layout.Root).Keys) + (copied > 0 ? "" : $" ({UI.SetupTexts.AlreadyThere(_lang)})"));

                step = Step("generation");
                if (state.Generated && MelonLoaderSetup.IsGenerated(clientRoot)) step.Skip(UI.SetupTexts.AlreadyThere(_lang));
                else
                {
                    step.Start(UI.SetupTexts.GenerationStage(_lang, "starting"));
                    var genStep = step;
                    await Guard(step, () => MelonLoaderSetup.GenerateAsync(client, layout.Root,
                        (f, stage) => genStep.Report(f, UI.SetupTexts.GenerationStage(_lang, stage)), cancel));
                    state.Generated = true;
                    layout.Save(state);
                    step.Finish();
                }
            }

            // 10. The world's database, unpacked now so the server's first start is quick.
            step = Step("database");
            string worldDb = Path.Combine(layout.Root, "bases", "world.db");
            string worldZip = Path.Combine(layout.Root, "datos", "world.zip");
            if (File.Exists(worldDb) || !File.Exists(worldZip)) step.Skip(UI.SetupTexts.AlreadyThere(_lang));
            else
            {
                step.Start();
                await Guard(step, () => Task.Run(() => ArchiveExtractor.Extract(worldZip, Path.Combine(layout.Root, "bases"), null, Bytes(step), cancel), cancel));
                step.Finish(Format.Bytes(new FileInfo(worldDb).Length));
            }

            // 11. The server, when it is this machine's: started now, so the launcher that opens
            //     next finds it answering and shows its sign-in at once.
            if (Steps.Any(s => s.Key == "server"))
            {
                step = Step("server");
                if (Network.ControlClient.ServidorVivo()) step.Skip(UI.SetupTexts.ServerAlreadyRunning(_lang));
                else
                {
                    step.Start(UI.SetupTexts.ServerStarting(_lang));
                    string server = layout.ServerExecutable;
                    bool answered = await Guard(step, () => Program.StartServerAsync(server, layout.Root));
                    if (!answered) throw Fail(step, UI.SetupTexts.ServerNoAnswer(_lang));
                    step.Finish(UI.SetupTexts.ServerOnline(_lang));
                }
            }

            // 12. Done.
            step = Step("finish");
            step.Start();
            layout.Save(state);
            Console.WriteLine($"[Setup] Installed: {state}");
            step.Finish(layout.Root);
        }

        /// <summary>The platform name the release's files are built for.</summary>
        public static string Platform => OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";

        /// <summary>The client the player pointed at: there, a supported version, and a folder MelonLoader can write in.</summary>
        private string CheckClient(SetupStep step, string executable)
        {
            executable = ClientCdn.ExecutableChosen(executable);
            if (!File.Exists(executable)) throw Fail(step, UI.SetupTexts.ClientNotFound(_lang));
            if (!ClientCdn.RunsHere(ClientCdn.PlatformOf(executable))) throw Fail(step, UI.SetupTexts.ClientCannotRunHere(_lang));
            string root = Path.GetDirectoryName(Path.GetFullPath(executable))!;
            string? version = Packs.ClientVersion.Read(root);
            if (version is not ("3.6.10.11" or "3.6.10.10"))
                throw Fail(step, UI.SetupTexts.WrongClientVersion(_lang, version ?? "?"));
            if (OperatingSystem.IsWindows() && SystemIntegration.NeedsElevation(root))
                throw Fail(step, UI.SetupTexts.ClientNotWritable(_lang));
            return Path.GetFullPath(executable);
        }

        private static IProgress<Transfer> Bytes(SetupStep step)
            => new Progress<Transfer>(t => step.Report(t.Fraction,
                $"{Format.Bytes(t.Done)} / {Format.Bytes(t.Total)}" + (t.BytesPerSecond > 0 ? $" · {Format.Speed(t.BytesPerSecond)}" : "")));

        /// <summary>The CDN's download on the step's bar: bytes and speed, or the part being checked.</summary>
        private IProgress<ClientCdnProgress> FromCdn(SetupStep step) => FromCdn(step, _lang);

        /// <summary>The same for a step of the settings' own download.</summary>
        internal static IProgress<ClientCdnProgress> FromCdn(SetupStep step, UI.Language lang)
            => new Progress<ClientCdnProgress>(p => step.Report(p.Fraction, p.Phase switch
            {
                Packs.PackPhase.Checking => UI.SetupTexts.CheckingWhatIsThere(lang, p.Fragment),
                Packs.PackPhase.Verifying => UI.SetupTexts.VerifyingPart(lang, p.Fragment),
                _ => $"{Format.Bytes((long)(p.Fraction * p.Total))} / {Format.Bytes(p.Total)}"
                     + (p.BytesPerSecond > 0 ? $" · {Format.Speed(p.BytesPerSecond)}" : ""),
            }));

        /// <summary>The client the README links, or null when it cannot be read or links none.</summary>
        private static async Task<(string Version, string Url)?> ReadmeLinkAsync(ReleaseFeed feed, string tag, CancellationToken cancel)
        {
            try
            {
                return ReleaseFeed.ClientLinkIn(await feed.ReadmeAsync(tag, cancel));
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Console.WriteLine($"[Setup] The README could not be read: {ex.Message}");
                return null;
            }
        }

        /// <summary>A part of the step's bar, from <paramref name="from"/> to <paramref name="to"/>.</summary>
        private static IProgress<Transfer> Scaled(SetupStep step, double from, double to)
            => new Progress<Transfer>(t => step.Report(from + (to - from) * t.Fraction,
                $"{Format.Bytes(t.Done)} / {Format.Bytes(t.Total)}" + (t.BytesPerSecond > 0 ? $" · {Format.Speed(t.BytesPerSecond)}" : "")));

        private static SetupException Fail(SetupStep step, string why)
        {
            step.Fail(why);
            return new SetupException(why);
        }

        /// <summary>Runs a step's work, marking the step failed with the reason when it throws.</summary>
        private static async Task<T> Guard<T>(SetupStep step, Func<Task<T>> work)
        {
            try { return await work(); }
            catch (OperationCanceledException) { step.Fail("—"); throw; }
            catch (SetupException ex) { step.Fail(ex.Message); throw; }
            catch (Exception ex) { step.Fail(ex.Message); throw new SetupException(ex.Message, ex); }
        }

        private static async Task Guard(SetupStep step, Func<Task> work)
            => await Guard<bool>(step, async () => { await work(); return true; });

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
