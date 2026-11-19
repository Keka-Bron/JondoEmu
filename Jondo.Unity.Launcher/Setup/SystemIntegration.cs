using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Jondo.Unity.Launcher.Setup
{
    /// <summary>
    /// What an installation leaves in the system beyond its folder: the folder itself where only
    /// an administrator may make one (Program Files), the right of the player to write in it,
    /// the shortcuts, the entry in Windows' installed apps, .NET when it is missing. And the way
    /// back: the uninstall.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On Windows that is the one moment the launcher asks for an administrator: it starts
    /// itself again elevated, with <c>--elevated-setup</c> and what to do, does it without a
    /// window, and ends. Everything after -- the downloads, MelonLoader, the updates -- runs as
    /// the player, who has been given write access to the folder: no update asks again.
    /// </para>
    /// <para>
    /// On Linux the folder is in the user's home, and the shortcut a .desktop file.
    /// </para>
    /// </remarks>
    internal static class SystemIntegration
    {
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\JondoEmu";
        public const string DisplayName = "Jondo Emulator";

        /// <summary>Whether making the folder needs an administrator: Program Files does, the user's own folders do not.</summary>
        public static bool NeedsElevation(string root)
        {
            if (!OperatingSystem.IsWindows()) return false;
            // The folder itself when it is there; else the nearest one above it, where it would be made.
            string probeDir = Path.GetFullPath(root);
            while (!Directory.Exists(probeDir))
            {
                string? up = Path.GetDirectoryName(probeDir);
                if (string.IsNullOrEmpty(up)) return true;
                probeDir = up;
            }
            return !CanWriteInside(probeDir);
        }

        private static bool CanWriteInside(string root)
        {
            if (!Directory.Exists(root)) return false;
            try
            {
                string probe = Path.Combine(root, ".jondo-write-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { return false; }
        }

        /// <summary>What the elevated part is asked to do.</summary>
        internal sealed class Request
        {
            public string Root { get; set; } = "";
            public string UserSid { get; set; } = "";
            public string DotNetInstaller { get; set; } = "";
            public string Version { get; set; } = "";
            public bool Shortcuts { get; set; } = true;

            public string ToArguments()
                => $"--elevated-setup \"{Root}\" \"{UserSid}\" \"{DotNetInstaller}\" \"{Version}\" {(Shortcuts ? 1 : 0)}";

            public static Request? FromArguments(string[] args)
            {
                int at = Array.IndexOf(args, "--elevated-setup");
                if (at < 0 || args.Length < at + 6) return null;
                return new Request
                {
                    Root = args[at + 1], UserSid = args[at + 2], DotNetInstaller = args[at + 3],
                    Version = args[at + 4], Shortcuts = args[at + 5] == "1",
                };
            }
        }

        /// <summary>
        /// Does what needs an administrator, asking for one when the launcher is not: one UAC
        /// prompt. False when the player said no.
        /// </summary>
        public static bool RunPrivileged(Request request)
        {
            if (!OperatingSystem.IsWindows()) { Prepare(request); return true; }
            if (!NeedsElevation(request.Root) && request.DotNetInstaller.Length == 0)
            {
                Prepare(request);
                return true;
            }
            try
            {
                using var elevated = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, request.ToArguments())
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                }) ?? throw new SetupException("The installer could not ask for permission.");
                elevated.WaitForExit();
                if (elevated.ExitCode != 0)
                {
                    string log = Path.Combine(Path.GetTempPath(), "jondo-elevated-setup.log");
                    string why = File.Exists(log) ? File.ReadAllText(log).Trim() : $"code {elevated.ExitCode}";
                    throw new SetupException($"The part of the installation that needs an administrator failed: {why}");
                }
                return true;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED: the player said no to the UAC prompt.
                return false;
            }
        }

        /// <summary>The elevated process's whole life: do it, write why not if it fails, end.</summary>
        public static int ElevatedMain(string[] args)
        {
            string log = Path.Combine(Path.GetTempPath(), "jondo-elevated-setup.log");
            try
            {
                var request = Request.FromArguments(args) ?? throw new ArgumentException("bad --elevated-setup arguments");
                Prepare(request);
                if (File.Exists(log)) File.Delete(log);
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(log, ex.Message); } catch { }
                return 1;
            }
        }

        /// <summary>The folder, the player's rights in it, .NET, the entry and the shortcuts.</summary>
        private static void Prepare(Request request)
        {
            Directory.CreateDirectory(request.Root);
            if (OperatingSystem.IsWindows())
            {
                if (request.UserSid.Length > 0) GiveThePlayerTheFolder(request.Root, request.UserSid);
                if (request.DotNetInstaller.Length > 0) DotNetRuntime.Install(request.DotNetInstaller);
                Register(request.Root, request.Version);
                if (request.Shortcuts) Shortcuts(request.Root, allUsers: IsElevated());
            }
            else
            {
                if (request.DotNetInstaller.Length > 0) DotNetRuntime.Install(request.DotNetInstaller);
                if (request.Shortcuts) LinuxShortcut(request.Root);
            }
        }

        /// <summary>The SID of the player running the launcher, for the elevated part to give the folder to.</summary>
        [SupportedOSPlatform("windows")]
        public static string CurrentUserSid() => WindowsIdentity.GetCurrent().User?.Value ?? "";

        [SupportedOSPlatform("windows")]
        public static bool IsElevated()
            => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        /// <summary>
        /// The player's right to write in the folder, given once. A repair finds it given: the
        /// install handed the Program Files folder to him, so the repair asks for no administrator,
        /// and changing the folder's rights again as the player is refused ("Attempted to perform
        /// an unauthorized operation") -- writing in it lets him change what is inside, not who may.
        /// Returns whether the rights were changed.
        /// </summary>
        [SupportedOSPlatform("windows")]
        internal static bool GiveThePlayerTheFolder(string root, string sid)
        {
            if (!IsElevated() && CanWriteInside(root)) return false;
            GrantModify(root, sid);
            return true;
        }

        /// <summary>Modify rights for the player on the folder and everything under it, now and to come.</summary>
        [SupportedOSPlatform("windows")]
        private static void GrantModify(string root, string sid)
        {
            var directory = new DirectoryInfo(root);
            var security = directory.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid),
                FileSystemRights.Modify | FileSystemRights.Synchronize,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(security);
        }

        /// <summary>The entry in Settings → Apps, with the launcher as the way to uninstall.</summary>
        [SupportedOSPlatform("windows")]
        public static void Register(string root, string version)
        {
            string launcher = Path.Combine(root, InstallLayout.LauncherFileName);
            bool elevated = IsElevated();
            if (!elevated)
            {
                // A repair run as the player of an installation registered for the machine: one
                // entry is enough, and the machine's already says it.
                using var machine = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(UninstallKey);
                if (machine != null) return;
            }
            var hive = elevated ? Microsoft.Win32.Registry.LocalMachine : Microsoft.Win32.Registry.CurrentUser;
            using var key = hive.CreateSubKey(UninstallKey);
            key.SetValue("DisplayName", DisplayName);
            key.SetValue("DisplayVersion", version.TrimStart('v'));
            key.SetValue("Publisher", "Jondo");
            key.SetValue("InstallLocation", root);
            key.SetValue("DisplayIcon", launcher);
            key.SetValue("UninstallString", $"\"{launcher}\" --uninstall");
            key.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
        }

        /// <summary>The version the entry shows, after an update.</summary>
        [SupportedOSPlatform("windows")]
        public static void UpdateRegisteredVersion(string version)
        {
            foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                try
                {
                    using var key = hive.OpenSubKey(UninstallKey, writable: true);
                    key?.SetValue("DisplayVersion", version.TrimStart('v'));
                }
                catch (UnauthorizedAccessException) { }
                catch (System.Security.SecurityException) { }
            }
        }

        /// <summary>Start menu and desktop shortcuts to the launcher.</summary>
        [SupportedOSPlatform("windows")]
        private static void Shortcuts(string root, bool allUsers)
        {
            string launcher = Path.Combine(root, InstallLayout.LauncherFileName);
            var places = new[]
            {
                (Own: Environment.SpecialFolder.Programs, Common: Environment.SpecialFolder.CommonPrograms),
                (Own: Environment.SpecialFolder.DesktopDirectory, Common: Environment.SpecialFolder.CommonDesktopDirectory),
            };
            foreach (var (own, common) in places)
            {
                string folder = Environment.GetFolderPath(allUsers ? common : own);
                if (string.IsNullOrEmpty(folder)) continue;
                // A repair run as the player keeps the shortcuts the install made for everybody:
                // one of his own would sit beside each of them.
                string everybody = Environment.GetFolderPath(common);
                if (!allUsers && everybody.Length > 0 && File.Exists(Path.Combine(everybody, DisplayName + ".lnk"))) continue;
                CreateShortcut(Path.Combine(folder, DisplayName + ".lnk"), launcher, root);
            }
        }

        /// <summary>Every shortcut the installer could have made, for the uninstall.</summary>
        public static string[] ShortcutPaths()
        {
            if (!OperatingSystem.IsWindows())
                return new[] { Path.Combine(UserDirectories.Data, "applications", "jondo-emulator.desktop") };
            return new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.Programs,
                           Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.DesktopDirectory }
                .Select(Environment.GetFolderPath).Where(f => f.Length > 0)
                .Select(f => Path.Combine(f, DisplayName + ".lnk")).ToArray();
        }

        [SupportedOSPlatform("windows")]
        private static void CreateShortcut(string path, string target, string workingDirectory)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new SetupException("Windows has no WScript.Shell.");
            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic link = shell.CreateShortcut(path);
                link.TargetPath = target;
                link.WorkingDirectory = workingDirectory;
                link.IconLocation = target + ",0";
                link.Description = DisplayName;
                link.Save();
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }

        private static void LinuxShortcut(string root)
        {
            string launcher = Path.Combine(root, InstallLayout.LauncherFileName);
            string file = ShortcutPaths()[0];
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var text = new StringBuilder()
                .AppendLine("[Desktop Entry]").AppendLine("Type=Application")
                .AppendLine($"Name={DisplayName}").AppendLine($"Exec=\"{launcher}\"")
                .AppendLine($"Path={root}").AppendLine("Terminal=false").AppendLine("Categories=Game;");
            string icon = Path.Combine(root, "launcher_assets", "icon.png");
            if (File.Exists(icon)) text.AppendLine($"Icon={icon}");
            File.WriteAllText(file, text.ToString());
            File.SetUnixFileMode(file, File.GetUnixFileMode(file) | UnixFileMode.UserExecute);
        }
    }
}
