using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jondo.Unity.Launcher.Setup
{
    /// <summary>
    /// MelonLoader in the client: the version the emulator's mods are built against, its files
    /// next to <c>Dofus.exe</c>, the mods in <c>Mods\</c>, and its first run, where it dumps the
    /// game and generates the assemblies the mods are compiled against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first run is the long one: MelonLoader downloads Cpp2IL and Unity's libraries, runs
    /// Cpp2IL over GameAssembly.dll, then Il2CppInterop over what Cpp2IL dumped; several minutes
    /// with nothing on the screen but a black window. It writes every stage to
    /// <c>MelonLoader\Latest.log</c>, and the stages are what the step's bar follows. Once the
    /// mods are loaded the client has nothing left to do here, and it is closed.
    /// </para>
    /// <para>
    /// Only on Windows: the native Linux client is played without mods.
    /// </para>
    /// </remarks>
    internal static class MelonLoaderSetup
    {
        public const string Version = "0.7.3";
        public const string Asset = "MelonLoader.x64.zip";
        public const long AssetSize = 20155622;
        public static string Url => $"https://github.com/LavaGang/MelonLoader/releases/download/v{Version}/{Asset}";

        /// <summary>Whether MelonLoader is in the client, and which version its files say.</summary>
        public static bool IsInstalled(string clientRoot)
            => File.Exists(Path.Combine(clientRoot, "version.dll"))
               && File.Exists(Path.Combine(clientRoot, "MelonLoader", "net6", "MelonLoader.dll"));

        /// <summary>Whether the assemblies have been generated once already.</summary>
        public static bool IsGenerated(string clientRoot)
            => Directory.Exists(Path.Combine(clientRoot, "MelonLoader", "Il2CppAssemblies"))
               && Directory.EnumerateFiles(Path.Combine(clientRoot, "MelonLoader", "Il2CppAssemblies"), "*.dll").Any();

        /// <summary>Downloads MelonLoader's zip into the downloads folder.</summary>
        public static Task DownloadAsync(string downloads, IProgress<Transfer> progress, CancellationToken cancel)
            => Downloader.DownloadAsync(Url, Path.Combine(downloads, Asset), AssetSize, null, progress, cancel);

        /// <summary>Unpacks it beside Dofus.exe: version.dll, which loads it, and the MelonLoader folder.</summary>
        public static void Unpack(string downloads, string clientRoot, IProgress<Transfer> progress, CancellationToken cancel)
        {
            string zip = Path.Combine(downloads, Asset);
            if (!File.Exists(zip)) throw new SetupException("MelonLoader's download is missing.");
            // A version.dll put aside to play the official game is put back by the new one.
            string disabled = Path.Combine(clientRoot, "version.dll.disabled");
            if (File.Exists(disabled)) File.Delete(disabled);
            ArchiveExtractor.Extract(zip, clientRoot, null, progress, cancel);
            if (!IsInstalled(clientRoot)) throw new SetupException("MelonLoader did not unpack where it should.");
        }

        /// <summary>The emulator's mods into the client's Mods folder, made if it is not there.</summary>
        public static int InstallMods(string emulatorRoot, string clientRoot)
        {
            string mods = Path.Combine(clientRoot, "Mods");
            Directory.CreateDirectory(mods);
            int copied = 0;
            foreach ((string mod, string shipped) in BundledClientMods.Discover(emulatorRoot))
            {
                string installed = Path.Combine(mods, mod);
                if (!File.Exists(installed) || !BundledClientMods.SameContent(installed, shipped))
                {
                    File.Copy(shipped, installed + ".jondo-new", overwrite: true);
                    ArchiveExtractor.ReplaceFile(installed + ".jondo-new", installed);
                    copied++;
                }
                BundledClientMods.Remember(clientRoot, mod);
            }
            return copied;
        }

        /// <summary>
        /// The stages MelonLoader's log goes through on a first run, each with where the bar is
        /// when it appears and how long it lasts before the next, as measured on this client.
        /// </summary>
        internal static readonly (string Marker, double At, double Until, double Seconds, string Stage)[] Stages =
        {
            ("Loading Il2CppAssemblyGenerator", 0.01, 0.03, 3, "starting"),
            ("Contacting RemoteAPI", 0.03, 0.05, 3, "remoteapi"),
            ("Downloading Cpp2IL", 0.05, 0.09, 15, "cpp2il-download"),
            ("Extracting Cpp2IL", 0.09, 0.11, 5, "cpp2il-extract"),
            ("Downloading Cpp2IL.Plugin", 0.11, 0.12, 5, "plugin-download"),
            ("Downloading UnityDependencies", 0.12, 0.16, 15, "unity-download"),
            ("Extracting UnityDependencies", 0.16, 0.18, 5, "unity-extract"),
            ("Checking GameAssembly", 0.18, 0.20, 3, "checking"),
            ("Assembly Generation Needed", 0.20, 0.56, 240, "cpp2il"),
            ("Reading dumped assemblies", 0.56, 0.60, 20, "reading"),
            ("Generating Interop Assemblies", 0.60, 0.92, 180, "interop"),
            ("Interop Generation Complete", 0.92, 0.94, 10, "interop-done"),
            ("Cleaning up", 0.94, 0.96, 10, "cleaning"),
            ("Assembly Generation Successful", 0.96, 0.98, 10, "generated"),
            ("No Generation Needed", 0.96, 0.98, 5, "generated"),
            ("Loading Mods", 0.98, 0.995, 10, "mods"),
        };

        /// <summary>Where a first run stands from the lines of its log so far, and its stage.</summary>
        internal static (double Fraction, string Stage, bool Done, string? Error) Read(string[] lines, TimeSpan sinceStage)
        {
            int last = -1;
            bool done = false;
            string? error = null;
            foreach (string line in lines)
            {
                for (int i = 0; i < Stages.Length; i++)
                    if (line.Contains(Stages[i].Marker, StringComparison.Ordinal) && i > last) last = i;
                if (line.Contains("Mods loaded.", StringComparison.Ordinal) || line.Contains("Mod loaded.", StringComparison.Ordinal)
                    || line.Contains("Support Module Loaded", StringComparison.Ordinal))
                    done = true;
                if (line.Contains("Error Generating Interop Assemblies", StringComparison.Ordinal)
                    || line.Contains("Failed to Generate", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Cpp2IL Failed", StringComparison.OrdinalIgnoreCase))
                    error = line.Trim();
            }
            if (done) return (1, "done", true, error);
            if (last < 0) return (0, "starting", false, error);
            var stage = Stages[last];
            // Within a stage the bar creeps toward its end, ever slower, so it never stops and never passes it.
            double creep = 1 - Math.Exp(-sinceStage.TotalSeconds / Math.Max(1, stage.Seconds));
            return (stage.At + (stage.Until - stage.At) * creep, stage.Stage, false, error);
        }

        /// <summary>
        /// Runs the client once with MelonLoader, out of sight, following its log, until the mods
        /// are loaded; then closes it. <paramref name="report"/> gets the fraction and the stage.
        /// </summary>
        /// <remarks>
        /// Nothing shows on screen: Unity runs in batch mode with no graphics device, so it opens no
        /// window, and MelonLoader keeps its console and its start screen hidden. The generation
        /// happens when il2cpp starts, before anything would be drawn, so it is the same work.
        /// </remarks>
        /// <summary>The first run's arguments: Unity without a window, MelonLoader without its console or start screen.</summary>
        internal static readonly string[] HiddenRun =
            { "-batchmode", "-nographics", "--melonloader.hideconsole", "--melonloader.disablestartscreen" };

        public static async Task GenerateAsync(string clientExecutable, string emulatorRoot,
                                               Action<double, string> report, CancellationToken cancel)
        {
            string clientRoot = Path.GetDirectoryName(clientExecutable)!;
            string log = Path.Combine(clientRoot, "MelonLoader", "Latest.log");
            var started = DateTime.Now;
            var start = new ProcessStartInfo(clientExecutable)
            {
                WorkingDirectory = clientRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            foreach (string argument in HiddenRun) start.ArgumentList.Add(argument);
            // The mods find the emulator's data; the client is closed before it reaches its login.
            start.Environment["JONDO_EMULATOR_ROOT"] = emulatorRoot;
            start.Environment.Remove(ClientModManifest.LaunchManifestEnvironmentVariable);

            using var client = Process.Start(start) ?? throw new SetupException("The client did not start.");
            Console.WriteLine($"[Setup] First run of the client with MelonLoader (PID {client.Id}).");
            string stage = "";
            var stageSince = DateTime.Now;
            var deadline = DateTime.Now.AddMinutes(45);
            try
            {
                while (true)
                {
                    await Task.Delay(500, cancel);
                    string[] lines = ReadShared(log, started);
                    var (fraction, now, done, error) = Read(lines, DateTime.Now - stageSince);
                    if (now != stage) { stage = now; stageSince = DateTime.Now; }
                    report(fraction, stage);
                    if (error != null) throw new SetupException($"MelonLoader could not generate the assemblies: {error}");
                    if (done)
                    {
                        Console.WriteLine($"[Setup] MelonLoader ready after {(DateTime.Now - started):mm\\:ss}.");
                        break;
                    }
                    if (client.HasExited)
                        throw new SetupException($"The client closed during MelonLoader's first run (code {client.ExitCode}); " +
                                                 $"see {log}.");
                    if (DateTime.Now > deadline)
                        throw new SetupException("MelonLoader's first run is taking more than 45 minutes.");
                }
            }
            finally
            {
                if (!client.HasExited)
                {
                    try { client.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    await client.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)).ContinueWith(_ => { });
                }
            }
            if (!IsGenerated(clientRoot)) throw new SetupException("MelonLoader finished without generating its assemblies.");
        }

        /// <summary>The log's lines if it was written by this run, read while MelonLoader still has it open.</summary>
        private static string[] ReadShared(string path, DateTime since)
        {
            try
            {
                if (!File.Exists(path) || File.GetLastWriteTime(path) < since.AddSeconds(-2)) return Array.Empty<string>();
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Split('\n');
            }
            catch (IOException)
            {
                return Array.Empty<string>();
            }
        }
    }
}
