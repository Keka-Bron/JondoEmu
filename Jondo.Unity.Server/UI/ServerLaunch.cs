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

    /// <summary>
    /// The exit code of a run that ends to be restarted by its supervisor (EX_TEMPFAIL).
    /// </summary>
    internal const int RestartExitCode = 75;

    /// <summary>
    /// Whether a restart ends this process and leaves starting the next run to its supervisor
    /// (<c>JONDO_RESTART_MODE=exit</c>). In a container the server is PID 1: a child it spawned
    /// would die with it, so Docker's restart policy has to start the next run instead.
    /// </summary>
    internal static bool RestartByExit() => RestartByExit(Environment.GetEnvironmentVariable("JONDO_RESTART_MODE"));

    internal static bool RestartByExit(string mode)
        => string.Equals((mode ?? "").Trim(), "exit", StringComparison.OrdinalIgnoreCase);

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
