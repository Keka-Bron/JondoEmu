namespace Jondo.Unity.Launcher;

/// <summary>The client location shared by the launcher, editor and sprite reader.</summary>
public sealed record ClientInstallation(string Executable)
{
    // Newest first: discovery and native-launch validation use the same supported versions.
    private static readonly string[] SupportedVersions = { "3.6.10.11", "3.6.10.10" };

    public const string ConfigUrl = "http://127.0.0.1:8888/config/dofus3.json";
    public static string ExecutableName => OperatingSystem.IsWindows() ? "Dofus.exe" : "Dofus.x64";
    public static string PreferencesFile => Path.Combine(
        UserDirectories.Configuration, "Jondo", "lanzador.cfg");
    public static string DefaultDirectory => OperatingSystem.IsWindows()
        ? Path.Combine(@"C:\Jondo", "DofusClient") : Path.Combine(Paths.Root, "client");
    public static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public bool IsWindows => Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>MelonLoader's Linux loader, preloaded into the native client to load the mods.</summary>
    public const string LinuxMelonLoader = "MelonLoader.Bootstrap.so";

    /// <summary>
    /// Whether the native Linux client carries MelonLoader, and so the mods: its loader beside
    /// <c>Dofus.x64</c> and its folder.
    /// </summary>
    /// <remarks>
    /// It needs an Il2CppInterop that resolves GetTypeInfoFromTypeDefinitionIndex on ELF builds
    /// (BepInEx/Il2CppInterop#296); with an older one the client dies on its first frames.
    /// </remarks>
    public bool HasLinuxMods => !IsWindows
                                && File.Exists(Path.Combine(Root, LinuxMelonLoader))
                                && Directory.Exists(Path.Combine(Root, "MelonLoader"));

    /// <summary>
    /// The data folder under the name Cpp2IL looks for, <c>Dofus.x64_Data</c>, Unity's
    /// «executable_Data»: the client ships it as <c>Dofus_Data</c>, and MelonLoader's first run
    /// cannot generate its assemblies without it. A link, made once.
    /// </summary>
    public void LinkDataFolderForMelonLoader()
    {
        string link = Path.Combine(Root, Path.GetFileName(Executable) + "_Data");
        if (Path.GetFullPath(link) == Path.GetFullPath(DataDirectory)) return;
        if (Directory.Exists(link) || File.Exists(link) || new FileInfo(link).LinkTarget != null) return;
        Directory.CreateSymbolicLink(link, Path.GetFileName(DataDirectory));
    }
    public string Platform => IsWindows ? "windows" : "linux";
    public string Root => Path.GetDirectoryName(Path.GetFullPath(Executable))!;
    public string DataDirectory => Path.Combine(Root, "Dofus_Data");
    public string VersionFile => Path.Combine(DataDirectory, "StreamingAssets", "version");

    public string Setting(string key)
    {
        foreach (string line in File.ReadLines(VersionFile))
        {
            int equals = line.IndexOf('=');
            if (equals > 0 && line[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line[(equals + 1)..].Trim();
        }
        return "";
    }

    public void ValidateNativeLaunch()
    {
        if (!File.Exists(Executable)) throw new FileNotFoundException("Client executable not found", Executable);
        // The Windows client on Linux runs through Wine, for the mods the native one cannot load.
        if (WineClient.Runs(this)) WineClient.Validate(this);
        else if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(Executable) &
            (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
            throw new InvalidOperationException($"The client is not executable. Run chmod +x \"{Executable}\".");
        string version = Setting("Version");
        if (!SupportedVersions.Contains(version))
            throw new InvalidOperationException($"Unsupported client version {version}; expected {string.Join(" or ", SupportedVersions)}.");
        if (Setting("ConfigUrl") != ConfigUrl)
            throw new InvalidOperationException($"Set ConfigUrl={ConfigUrl} in {VersionFile}, using Settings → Configure client.");
    }

    /// <summary>Preserves the version and unrelated settings, keeping the original for recovery.</summary>
    public void Configure()
    {
        var lines = File.ReadAllLines(VersionFile).ToList();
        int index = lines.FindIndex(line => line.Split('=', 2)[0].Trim()
            .Equals("ConfigUrl", StringComparison.OrdinalIgnoreCase));
        if (index >= 0) lines[index] = "ConfigUrl=" + ConfigUrl;
        else lines.Add("ConfigUrl=" + ConfigUrl);
        if (!File.Exists(VersionFile + ".jondo-backup")) File.Copy(VersionFile, VersionFile + ".jondo-backup");
        string temporary = VersionFile + ".jondo-tmp";
        File.WriteAllLines(temporary, lines);
        File.Move(temporary, VersionFile, overwrite: true);
    }

    public static ClientInstallation? InDirectory(string root)
    {
        foreach (string name in new[] { ExecutableName, "Dofus.x64", "Dofus.exe" }.Distinct())
        {
            string executable = Path.Combine(root, name);
            if (File.Exists(executable)) return new(Path.GetFullPath(executable));
        }
        return null;
    }

    public static ClientInstallation? Find()
    {
        string? configured = Environment.GetEnvironmentVariable("JONDO_CLIENT_EXECUTABLE");
        if (string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                if (File.Exists(PreferencesFile))
                    configured = File.ReadLines(PreferencesFile)
                        .Where(line => line.Split('=', 2)[0].Trim().Equals("cliente", StringComparison.OrdinalIgnoreCase))
                        .Select(line => line.Split('=', 2).ElementAtOrDefault(1)?.Trim()).LastOrDefault();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var parents = new List<string> { Path.GetFullPath(Path.Combine(Paths.Root, "..")) };
        if (OperatingSystem.IsWindows()) parents.Add(Paths.LegacyRoot);
        return Find(configured, parents, DefaultDirectory);
    }

    internal static ClientInstallation? Find(string? configured, IEnumerable<string> parents, string fallbackDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return new(Path.GetFullPath(configured));
        foreach (string parent in parents)
            foreach (string version in SupportedVersions)
            {
                // The launcher downloads the Linux client beside the Windows one, under a name of
                // its own; on Linux it is the one preferred.
                if (!OperatingSystem.IsWindows() && InDirectory(Path.Combine(parent, "Cliente " + version + " Linux")) is { } native)
                    return native;
                if (InDirectory(Path.Combine(parent, "Cliente " + version)) is { } installation) return installation;
            }
        return InDirectory(fallbackDirectory);
    }
}
