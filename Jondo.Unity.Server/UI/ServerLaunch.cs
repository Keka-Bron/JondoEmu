using System.Diagnostics;

namespace Jondo.Unity.Server.UI;

/// <summary>Desktop selection and restart arguments, shared by both operating systems.</summary>
internal static class ServerLaunch
{
    internal static bool Headless(string[] args) => Headless(args, OperatingSystem.IsLinux(),
        Environment.GetEnvironmentVariable("DISPLAY"));

    internal static bool Headless(string[] args, bool linux, string display)
        => args.Contains("--headless", StringComparer.Ordinal) ||
           // Avalonia 11's Linux desktop uses X11, including XWayland in Wayland sessions.
           (linux && string.IsNullOrWhiteSpace(display));

    internal static ProcessStartInfo Restart(string executable, string assembly, int previous, bool headless)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        // Framework-dependent `dotnet server.dll` has a different process path from an apphost.
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.GetFullPath(assembly));
        if (headless) start.ArgumentList.Add("--headless");
        start.ArgumentList.Add("--restart-after");
        start.ArgumentList.Add(previous.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return start;
    }
}
