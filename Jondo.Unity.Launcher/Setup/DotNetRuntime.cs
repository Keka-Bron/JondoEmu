using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jondo.Unity.Launcher.Setup
{
    /// <summary>
    /// The .NET runtime the server, Studio and MelonLoader run on. The launcher carries its own
    /// and needs none; the others look for it on the machine (MelonLoader through hostfxr), so
    /// a machine without it gets it here, from Microsoft, checked against the hash Microsoft
    /// publishes.
    /// </summary>
    internal static class DotNetRuntime
    {
        public const string Channel = "10.0";
        private const string Metadata = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json";

        /// <summary>Where the machine's .NET lives on Windows; on Linux, the user's own when the system has none.</summary>
        public static string Root => OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet")
            : Directory.Exists("/usr/share/dotnet/shared/Microsoft.NETCore.App") ? "/usr/share/dotnet"
            : Directory.Exists("/usr/lib/dotnet/shared/Microsoft.NETCore.App") ? "/usr/lib/dotnet"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");

        /// <summary>Whether a 10.x runtime is there.</summary>
        public static bool IsInstalled()
        {
            foreach (string root in new[] { Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "", Root })
            {
                if (root.Length == 0) continue;
                string shared = Path.Combine(root, "shared", "Microsoft.NETCore.App");
                if (Directory.Exists(shared) && Directory.EnumerateDirectories(shared, Channel.Split('.')[0] + ".*").Any())
                    return true;
            }
            return false;
        }

        /// <summary>The installer of the latest 10.x runtime for this machine, and its SHA-512.</summary>
        public static async Task<(string Url, string Sha512, string Version)> LatestAsync(CancellationToken cancel)
        {
            string json = await Http.Shared.GetStringAsync(Metadata, cancel);
            using var doc = JsonDocument.Parse(json);
            string latest = doc.RootElement.GetProperty("latest-runtime").GetString() ?? "";
            string file = OperatingSystem.IsWindows() ? "dotnet-runtime-win-x64.exe" : "dotnet-runtime-linux-x64.tar.gz";
            foreach (var release in doc.RootElement.GetProperty("releases").EnumerateArray())
            {
                if (!release.TryGetProperty("runtime", out var runtime) || runtime.ValueKind != JsonValueKind.Object) continue;
                if (runtime.GetProperty("version").GetString() != latest) continue;
                foreach (var f in runtime.GetProperty("files").EnumerateArray())
                    if (f.GetProperty("name").GetString() == file)
                        return (f.GetProperty("url").GetString()!, f.GetProperty("hash").GetString()!, latest);
            }
            throw new SetupException($"Microsoft's list has no .NET {latest} runtime for this machine.");
        }

        /// <summary>Downloads the installer and checks it; returns where it is.</summary>
        public static async Task<string> DownloadAsync(string downloads, IProgress<Transfer> progress, CancellationToken cancel)
        {
            var (url, sha512, version) = await LatestAsync(cancel);
            string target = Path.Combine(downloads, Path.GetFileName(new Uri(url).LocalPath));
            await Downloader.DownloadAsync(url, target, 0, null, progress, cancel);
            await using (var stream = File.OpenRead(target))
            {
                string got = Convert.ToHexString(await SHA512.HashDataAsync(stream, cancel));
                if (!got.Equals(sha512, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(target);
                    throw new SetupException($"The .NET {version} installer arrived damaged.");
                }
            }
            Console.WriteLine($"[Setup] .NET {version} runtime downloaded and checked.");
            return target;
        }

        /// <summary>
        /// Runs Microsoft's installer quietly. On Windows it needs the administrator's rights the
        /// elevated part of the setup has; on Linux the archive goes into the user's ~/.dotnet.
        /// </summary>
        public static void Install(string installer)
        {
            if (OperatingSystem.IsWindows())
            {
                using var process = Process.Start(new ProcessStartInfo(installer, "/install /quiet /norestart")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }) ?? throw new SetupException("The .NET installer did not start.");
                process.WaitForExit();
                // 3010: installed, a restart pending; 1638: a newer one is there already.
                if (process.ExitCode is not (0 or 3010 or 1638))
                    throw new SetupException($"The .NET installer failed (code {process.ExitCode}).");
                return;
            }

            string into = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
            Directory.CreateDirectory(into);
            using var tar = Process.Start(new ProcessStartInfo("tar", $"-xzf \"{installer}\" -C \"{into}\"") { UseShellExecute = false })
                            ?? throw new SetupException("tar did not start.");
            tar.WaitForExit();
            if (tar.ExitCode != 0) throw new SetupException($"The .NET archive could not be unpacked (tar {tar.ExitCode}).");
        }
    }
}
