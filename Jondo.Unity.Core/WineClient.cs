using System.Diagnostics;

namespace Jondo.Unity.Launcher;

/// <summary>
/// The Windows client on Linux, through Wine, so that MelonLoader and the emulator's mods run
/// there as they do on Windows.
/// </summary>
/// <remarks>
/// <para>
/// The native Linux client cannot load the mods: MelonLoader 0.7.3 does start on it and generates
/// its assemblies, but Il2CppInterop finds IL2CPP's internals by byte signatures taken from Windows
/// builds, they miss on Unity 6000.3's ELF build, and the process dies on the substitute
/// (BepInEx/Il2CppInterop#283). The Windows client under Wine has none of that: it is the same
/// GameAssembly.dll the mods are built against.
/// </para>
/// <para>
/// What each setting is for, every one of them found by a launch that failed without it:
/// <list type="bullet">
/// <item><c>version=n,b</c>: Wine loads its own version.dll otherwise, and MelonLoader's proxy never runs.
/// <c>mscoree</c> must stay enabled: Wine's loader hands .NET images to it, and Cpp2IL cannot load
/// its plugin without it.</item>
/// <item><c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT</c>: Cpp2IL dies on Wine's ICU
/// («Cannot get symbol u_charsToUChars from libicuuc»).</item>
/// <item><c>DOTNET_GCHeapHardLimit</c>: .NET misreads the memory Wine reports and the interop
/// generation runs out of it.</item>
/// </list>
/// Once, on the very first start, the one that also generated the assemblies, the game got an
/// empty display list from Wine and <c>DisplayConfiguration.GetDisplayInfo</c> threw on its loading
/// screen; it never came back after, with Wine's X11 driver or its Wayland one. <c>JONDO_WINE_DESKTOP=1</c>
/// puts each client in a virtual desktop of its own, which avoided it, should it ever return.
/// </para>
/// </remarks>
public static class WineClient
{
    /// <summary>The program that starts Windows executables, from the PATH unless <c>JONDO_WINE</c> names one.</summary>
    public static string Program => Environment.GetEnvironmentVariable("JONDO_WINE") is { Length: > 0 } wine ? wine : "wine";

    /// <summary>
    /// The emulator's own Wine prefix, apart from the player's default one, unless
    /// <c>JONDO_WINEPREFIX</c> names another.
    /// </summary>
    public static string Prefix => Environment.GetEnvironmentVariable("JONDO_WINEPREFIX") is { Length: > 0 } prefix
        ? Path.GetFullPath(prefix)
        : Path.Combine(UserDirectories.Data, "Jondo", "wine");

    /// <summary>The managed heap's ceiling, 10 GB: the interop generation peaks well below it.</summary>
    public const string HeapHardLimit = "0x280000000";

    /// <summary>Whether this client is started through Wine: a Windows client on another system.</summary>
    public static bool Runs(ClientInstallation client) => client.IsWindows && !OperatingSystem.IsWindows();

    /// <summary>Whether Wine can be started: <see cref="Program"/> as a path, or found on the PATH.</summary>
    public static bool IsAvailable()
    {
        string program = Program;
        if (program.Contains(Path.DirectorySeparatorChar)) return File.Exists(program);
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (folder.Length > 0 && File.Exists(Path.Combine(folder, program))) return true;
        return false;
    }

    /// <summary>A Linux path as Wine's Z: drive shows it to Windows programs.</summary>
    public static string WindowsPath(string path) => "Z:" + Path.GetFullPath(path).Replace('/', '\\');

    /// <summary>Whether each client gets a Wine virtual desktop of its own instead of a window on the real one.</summary>
    public static bool VirtualDesktop => Environment.GetEnvironmentVariable("JONDO_WINE_DESKTOP") is "1" or "true";

    /// <summary>
    /// Turns the start of a Windows client into its start through Wine: same arguments and
    /// environment, with the settings Wine needs; inside a virtual desktop only when asked.
    /// </summary>
    /// <param name="desktop">The virtual desktop's name: one per client, so each has its own window.</param>
    public static void Wrap(ProcessStartInfo start, ClientInstallation client, string desktop, int width, int height)
    {
        var arguments = start.ArgumentList.ToList();
        start.FileName = Program;
        start.ArgumentList.Clear();
        if (VirtualDesktop)
        {
            start.ArgumentList.Add("explorer");
            start.ArgumentList.Add($"/desktop={desktop},{width}x{height}");
        }
        start.ArgumentList.Add(WindowsPath(client.Executable));
        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        start.Environment["WINEPREFIX"] = Prefix;
        start.Environment["WINEDLLOVERRIDES"] = "version=n,b";
        start.Environment["WINEDEBUG"] = "-all";
        start.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1";
        start.Environment["DOTNET_GCHeapHardLimit"] = HeapHardLimit;
        if (start.Environment.TryGetValue("JONDO_EMULATOR_ROOT", out string? root) && !string.IsNullOrEmpty(root))
            start.Environment["JONDO_EMULATOR_ROOT"] = WindowsPath(root);
    }

    /// <summary>Throws why this client cannot be started through Wine; when it can, readies the prefix's folder.</summary>
    public static void Validate(ClientInstallation client)
    {
        if (!File.Exists(client.Executable)) throw new FileNotFoundException("Client executable not found", client.Executable);
        if (!IsAvailable())
            throw new InvalidOperationException("The Windows client needs Wine on Linux: install it (for example «sudo dnf install wine»), " +
                                                "set JONDO_WINE to its path, or select the native Dofus.x64.");
        if (!File.Exists(Path.Combine(client.Root, "version.dll")) || !Directory.Exists(Path.Combine(client.Root, "MelonLoader")))
            throw new InvalidOperationException($"MelonLoader is not installed in {client.Root}: without it the Windows client " +
                                                "has no mods, and the native Dofus.x64 is the one to use.");
        // Wine makes the prefix on its first start, but not the folders above it.
        Directory.CreateDirectory(Path.GetDirectoryName(Prefix)!);
    }
}
