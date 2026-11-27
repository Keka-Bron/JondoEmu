using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;

namespace Jondo.Unity.Launcher.Setup
{
    /// <summary>
    /// Takes an installation away: its folder (the emulator, its databases, the client it
    /// downloaded), its shortcuts, its entry in the installed apps, and the emulator's mods from
    /// a client the player brought, which is otherwise left as it was.
    /// </summary>
    /// <remarks>
    /// A program cannot delete the folder it runs from, so the launcher copies itself to the
    /// temporary folder and that copy does it, once the launcher has closed: elevated when the
    /// entry is the machine's (the setup registered it as administrator), as the player
    /// otherwise. The copy removes itself when it is done.
    /// </remarks>
    internal static class Uninstaller
    {
        /// <summary>Starts the copy that uninstalls; false when the player refused the administrator prompt.</summary>
        public static bool Start(InstallLayout layout)
        {
            string copy = Path.Combine(Path.GetTempPath(), "JondoEmu-uninstall" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            File.Copy(Environment.ProcessPath!, copy, overwrite: true);
            var start = new ProcessStartInfo(copy)
            {
                Arguments = $"--uninstall-run \"{layout.Root}\" --after {Environment.ProcessId}",
                UseShellExecute = true,
                WorkingDirectory = Path.GetTempPath(),
            };
            if (OperatingSystem.IsWindows() && MachineWide())
            {
                start.Verb = "runas";
                start.WindowStyle = ProcessWindowStyle.Hidden;
            }
            try
            {
                Process.Start(start);
                return true;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return false;
            }
        }

        /// <summary>Whether the setup registered it for the whole machine, which only an administrator undoes.</summary>
        private static bool MachineWide()
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(SystemIntegration.UninstallKey);
            return key != null;
        }

        /// <summary>The copy's whole life.</summary>
        public static int RunMain(string[] args)
        {
            string log = Path.Combine(Path.GetTempPath(), "jondo-uninstall.log");
            try
            {
                int at = Array.IndexOf(args, "--uninstall-run");
                if (at < 0 || at + 1 >= args.Length) return 2;
                string root = Path.GetFullPath(args[at + 1]);
                int after = Array.IndexOf(args, "--after");
                if (after >= 0 && after + 1 < args.Length && int.TryParse(args[after + 1], out int pid))
                {
                    try { using var launcher = Process.GetProcessById(pid); launcher.WaitForExit(30_000); }
                    catch (ArgumentException) { }
                }
                Remove(root);
                File.WriteAllText(log, "done");
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(log, ex.ToString()); } catch { }
                return 1;
            }
            finally
            {
                RemoveItselfLater();
            }
        }

        private static void Remove(string root)
        {
            var layout = new InstallLayout(root);
            // Only a folder an installation made: never a folder that merely was named.
            if (!File.Exists(layout.StateFile)) throw new InvalidOperationException($"{root} is not an installation.");
            var state = layout.Load();

            // What runs from it: the server, the launcher, Studio.
            foreach (string exe in new[] { layout.ServerExecutable, layout.LauncherExecutable,
                                           Path.Combine(root, OperatingSystem.IsWindows() ? "Jondo Studio.exe" : "Jondo Studio") })
                foreach (var process in Updater.ServersOf(exe))
                    using (process)
                    {
                        if (process.Id == Environment.ProcessId) continue;
                        try { process.Kill(entireProcessTree: true); process.WaitForExit(15_000); } catch (Exception) { }
                    }

            // The mods out of a client the player brought; one the installer downloaded goes with the folder.
            if (state.Client.Length > 0 && !IsInside(state.Client, root))
            {
                string clientRoot = Path.GetDirectoryName(state.Client) ?? "";
                string mods = Path.Combine(clientRoot, "Mods");
                var managed = BundledClientMods.Discover(root).Keys.Concat(BundledClientMods.ManagedNames(clientRoot));
                foreach (string mod in managed.Distinct(StringComparer.OrdinalIgnoreCase))
                    TryDelete(Path.Combine(mods, mod));
                string plugins = Path.Combine(Path.GetDirectoryName(state.Client) ?? "", "Plugins");
                TryDelete(Path.Combine(plugins, ClientModManifest.BootstrapFileName));
            }

            foreach (string shortcut in SystemIntegration.ShortcutPaths()) TryDelete(shortcut);
            if (OperatingSystem.IsWindows()) Unregister();

            // A downloaded client outside the folder (an older choice) goes too.
            if (state.ClientDownloaded && state.Client.Length > 0 && !IsInside(state.Client, root))
            {
                string client = Path.GetDirectoryName(state.Client) ?? "";
                if (client.Length > 0 && Directory.Exists(client)) TryDeleteDirectory(client);
            }
            TryDeleteDirectory(root);
            Console.WriteLine($"[Uninstall] {root} removed.");
        }

        [SupportedOSPlatform("windows")]
        private static void Unregister()
        {
            foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                try { hive.DeleteSubKeyTree(SystemIntegration.UninstallKey, throwOnMissingSubKey: false); }
                catch (UnauthorizedAccessException) { }
                catch (System.Security.SecurityException) { }
            }
        }

        private static bool IsInside(string path, string root)
            => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                                                 StringComparison.OrdinalIgnoreCase);

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        private static void TryDeleteDirectory(string path)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (!Directory.Exists(path)) return;
                    foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories)
                                                                 .Where(f => f.IsReadOnly))
                        file.IsReadOnly = false;
                    Directory.Delete(path, recursive: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    System.Threading.Thread.Sleep(1000);
                }
            }
        }

        /// <summary>The temporary copy cannot delete itself while it runs: a shell does it a moment after.</summary>
        private static void RemoveItselfLater()
        {
            string self = Environment.ProcessPath ?? "";
            if (self.Length == 0) return;
            try
            {
                var start = OperatingSystem.IsWindows()
                    ? new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 4 > nul & del /f /q \"{self}\"")
                    : new ProcessStartInfo("/bin/sh", $"-c \"sleep 3; rm -f '{self}'\"");
                start.CreateNoWindow = true;
                start.UseShellExecute = false;
                start.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(start);
            }
            catch (Exception) { }
        }
    }
}
