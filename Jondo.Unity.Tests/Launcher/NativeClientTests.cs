using Jondo.Unity.Deobfuscator;
using Jondo.Unity.Launcher;
using Jondo.Unity.Launcher.Packs;
using Jondo.Unity.Launcher.Setup;
using Jondo.Unity.Reversing;
using Xunit;

namespace Jondo.Unity.Tests.Launcher;

public sealed class NativeClientTests : IDisposable
{
#if JONDO_PROTOCOL_3_8_1_2
    private const string LaunchVersion = "3.8.1.2";
#else
    private const string LaunchVersion = "3.6.10.10";
#endif
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jondo native client é " + Guid.NewGuid().ToString("N"));
    public NativeClientTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ClientInstallation Client(string name = "Dofus.x64", string version = "3.6.10.10")
    {
        string executable = Path.Combine(_root, name);
        File.WriteAllText(executable, "");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var client = new ClientInstallation(executable);
        Directory.CreateDirectory(Path.GetDirectoryName(client.VersionFile)!);
        File.WriteAllText(client.VersionFile, $"Version={version}\nBuildDate=original\nConfigUrl=https://original\nOther=keep\n");
        return client;
    }

    [Fact]
    public void Configuring_preserves_unrelated_settings_and_an_original_backup()
    {
        var client = Client(version: LaunchVersion);
        string original = File.ReadAllText(client.VersionFile);
        Assert.Contains(client.VersionFile, Assert.Throws<InvalidOperationException>(client.ValidateNativeLaunch).Message);
        client.Configure(); client.Configure();
        Assert.Equal(original, File.ReadAllText(client.VersionFile + ".jondo-backup"));
        Assert.Equal("original", client.Setting("BuildDate"));
        Assert.Equal("keep", client.Setting("Other"));
        Assert.Equal(ClientInstallation.ConfigUrl, client.Setting("ConfigUrl"));
        client.ValidateNativeLaunch();
        Assert.Equal(client.Root, ClientInstallation.InDirectory(_root)!.Root);
    }

    [Fact]
    public void Unsupported_clients_fail_before_the_server_allocates_an_account_slot()
    {
        var client = Client();
        client.Configure();
        string otherProfile = LaunchVersion == "3.8.1.2" ? "3.6.10.10" : "3.8.1.2";
        File.WriteAllText(client.VersionFile, $"Version={otherProfile}\nConfigUrl=" + ClientInstallation.ConfigUrl);
        Assert.Contains("Unsupported client version", Assert.Throws<InvalidOperationException>(client.ValidateNativeLaunch).Message);
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(client.Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Contains("chmod +x", Assert.Throws<InvalidOperationException>(client.ValidateNativeLaunch).Message);
    }

    [Fact]
    public void Native_launch_arguments_preserve_paths_and_credentials_without_windows_flags()
    {
        var client = Client();
        string? wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", "wayland-test");
        System.Diagnostics.ProcessStartInfo start;
        try
        {
            start = LauncherService.ClientStartInfo(client.Executable, 1280, 720, 17,
                "hash with spaces", "fr", "--hdReady", Path.Combine(_root, "shared preferences"), Path.Combine(_root, "logs"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", wayland);
        }
        Assert.False(start.UseShellExecute);
        Assert.Equal(client.Root, start.WorkingDirectory);
        Assert.Equal("hash with spaces", start.Environment["ZAAP_HASH"]);
        Assert.Equal("15881", start.Environment["ZAAP_PORT"]);
        Assert.Contains("hash with spaces", start.ArgumentList);
        Assert.DoesNotContain("-force-d3d11", start.ArgumentList);
        Assert.DoesNotContain(start.ArgumentList, arg => arg.Contains("melonloader"));
        Assert.Contains("--hdReady", start.ArgumentList);
        Assert.True(Directory.Exists(start.Environment["XDG_CONFIG_HOME"]));
        // The screen plugin must not take its Wayland path: it cannot find the window there.
        Assert.False(start.Environment.ContainsKey("WAYLAND_DISPLAY"));
        Assert.Contains(Path.Combine(start.Environment["ZAAP_LOGS_PATH"]!, "player.log"), start.ArgumentList);
        var second = LauncherService.ClientStartInfo(client.Executable, 1280, 720, 18,
            "other", "fr", "", start.Environment["XDG_CONFIG_HOME"]!, Path.Combine(_root, "logs"));
        Assert.Equal(start.Environment["XDG_CONFIG_HOME"], second.Environment["XDG_CONFIG_HOME"]);
        Assert.NotEqual(start.Environment["ZAAP_LOGS_PATH"], second.Environment["ZAAP_LOGS_PATH"]);
    }

    [Fact]
    public void Windows_launch_retains_its_mod_loader_and_graphics_options()
    {
        var client = Client("Dofus.exe");
        var start = LauncherService.ClientStartInfo(client.Executable, 1920, 1080, 19, "hash", "en", "", "", "");
        Assert.Contains("-force-d3d11", start.ArgumentList);
        Assert.Contains("--melonloader.hideconsole", start.ArgumentList);
        Assert.Contains("--melonloader.disablestartscreen", start.ArgumentList);
        Assert.DoesNotContain("-logFile", start.ArgumentList);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Native_launch_skips_mod_preparation_when_the_loader_is_missing_incomplete_or_disabled(
        bool hasBootstrap, bool hasManagedLoader, bool enableMods)
    {
        var client = Client(version: LaunchVersion);
        client.Configure();
        client.ValidateNativeLaunch();
        if (hasBootstrap) File.WriteAllText(Path.Combine(client.Root, ClientInstallation.LinuxMelonLoader), "");
        if (hasBootstrap) Directory.CreateDirectory(Path.Combine(client.Root, "MelonLoader", "net6"));
        if (hasManagedLoader) File.WriteAllText(Path.Combine(client.Root, "MelonLoader", "net6", "MelonLoader.dll"), "");

        var plan = ClientModSync.Prepare("invalid host", client, enableMods,
            (_, _, _) => throw new InvalidOperationException("Unexpected approval"));
        Assert.Null(plan);

        var start = LauncherService.ClientStartInfo(client.Executable, 1280, 720, 24, "hash", "fr", "",
            Path.Combine(_root, "config"), Path.Combine(_root, "logs"), preloadNativeMods: plan != null);
        Assert.DoesNotContain(start.ArgumentList, arg => arg.Contains("melonloader"));
        start.Environment.TryGetValue("LD_PRELOAD", out string? preload);
        Assert.Equal(Environment.GetEnvironmentVariable("LD_PRELOAD"), preload);
        Assert.False(start.Environment.ContainsKey(ClientModManifest.LaunchManifestEnvironmentVariable));
        Assert.False(start.Environment.ContainsKey("JONDO_PLAYER_TOKEN"));
        Assert.False(start.Environment.ContainsKey("JONDO_CONTROL_TOKEN"));
        Assert.False(Directory.Exists(Path.Combine(client.Root, "Mods")));
        Assert.False(Directory.Exists(Path.Combine(client.Root, "Plugins")));
    }

    [Fact]
    public void The_native_client_with_melonloader_preloads_it_relative_to_its_folder()
    {
        if (OperatingSystem.IsWindows()) return;
        var client = Client();
        Assert.False(client.HasLinuxMods);
        File.WriteAllText(Path.Combine(client.Root, ClientInstallation.LinuxMelonLoader), "");
        Assert.False(client.HasLinuxMods);
        string managed = Directory.CreateDirectory(Path.Combine(client.Root, "MelonLoader", "net6")).FullName;
        Assert.False(client.HasMelonLoader);
        File.WriteAllText(Path.Combine(managed, "MelonLoader.dll"), "");
        Assert.True(client.HasMelonLoader);

        string? before = Environment.GetEnvironmentVariable("LD_PRELOAD");
        try
        {
            Environment.SetEnvironmentVariable("LD_PRELOAD", null);
            var start = LauncherService.ClientStartInfo(client.Executable, 1280, 720, 22, "hash", "fr", "",
                Path.Combine(_root, "config"), Path.Combine(_root, "logs"), preloadNativeMods: client.HasMelonLoader);
            // The client's folder has a space in it ("jondo native client é …"): relative, from the working directory.
            Assert.Equal("./MelonLoader.Bootstrap.so", start.Environment["LD_PRELOAD"]);
            Assert.Equal(client.Root, start.WorkingDirectory);
            Assert.Equal(client.Executable, start.FileName);
            Assert.Contains("--melonloader.hideconsole", start.ArgumentList);
            Assert.DoesNotContain("-force-d3d11", start.ArgumentList);
            Assert.False(start.Environment.ContainsKey("WAYLAND_DISPLAY"));

            Environment.SetEnvironmentVariable("LD_PRELOAD", "/usr/lib64/libother.so");
            start = LauncherService.ClientStartInfo(client.Executable, 1280, 720, 23, "hash", "fr", "",
                Path.Combine(_root, "config"), Path.Combine(_root, "logs"), preloadNativeMods: client.HasMelonLoader);
            Assert.Equal("./MelonLoader.Bootstrap.so /usr/lib64/libother.so", start.Environment["LD_PRELOAD"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LD_PRELOAD", before);
        }
    }

    [Fact]
    public void The_data_folder_gets_the_name_cpp2il_looks_for_once()
    {
        if (OperatingSystem.IsWindows()) return;
        var client = Client();
        string link = Path.Combine(client.Root, "Dofus.x64_Data");
        client.LinkDataFolderForMelonLoader();
        Assert.Equal("Dofus_Data", new DirectoryInfo(link).LinkTarget);
        Assert.True(File.Exists(Path.Combine(link, "StreamingAssets", "version")));
        client.LinkDataFolderForMelonLoader();
        Assert.Equal("Dofus_Data", new DirectoryInfo(link).LinkTarget);
    }

    [Fact]
    public void The_windows_client_on_linux_starts_through_wine_with_its_mods_settings()
    {
        if (OperatingSystem.IsWindows()) return;
        var client = Client("Dofus.exe");
        var start = LauncherService.ClientStartInfo(client.Executable, 1600, 900, 21, "hash", "fr", "", "", Path.Combine(_root, "logs"));
        var arguments = start.ArgumentList.ToList();

        Assert.Equal(WineClient.Program, start.FileName);
        Assert.Equal("Z:" + client.Executable.Replace('/', '\\'), arguments[0]);
        Assert.DoesNotContain("explorer", arguments);
        // The client's own arguments follow, unchanged, mods and Direct3D included.
        Assert.Contains("-force-d3d11", arguments);
        Assert.Contains("--melonloader.hideconsole", arguments);
        Assert.Equal("hash", arguments[arguments.IndexOf("--hash") + 1]);
        Assert.DoesNotContain("-logFile", arguments);

        Assert.Equal(client.Root, start.WorkingDirectory);
        Assert.Equal(WineClient.Prefix, start.Environment["WINEPREFIX"]);
        // mscoree stays on: Wine's loader needs it for the .NET images Cpp2IL loads.
        Assert.Equal("version=n,b", start.Environment["WINEDLLOVERRIDES"]);
        Assert.Equal("1", start.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"]);
        Assert.Equal(WineClient.HeapHardLimit, start.Environment["DOTNET_GCHeapHardLimit"]);
        Assert.StartsWith("Z:\\", start.Environment["JONDO_EMULATOR_ROOT"]);
        Assert.Equal("hash", start.Environment["ZAAP_HASH"]);
    }

    [Fact]
    public void A_wine_virtual_desktop_is_one_per_client_and_only_when_asked()
    {
        if (OperatingSystem.IsWindows()) return;
        var client = Client("Dofus.exe");
        string? before = Environment.GetEnvironmentVariable("JONDO_WINE_DESKTOP");
        try
        {
            Environment.SetEnvironmentVariable("JONDO_WINE_DESKTOP", "1");
            var arguments = LauncherService.ClientStartInfo(client.Executable, 1600, 900, 21, "hash", "fr", "", "",
                Path.Combine(_root, "logs")).ArgumentList.ToList();
            Assert.Equal(new[] { "explorer", "/desktop=Jondo21,1600x900", "Z:" + client.Executable.Replace('/', '\\') },
                arguments.Take(3));
            Assert.Contains("-force-d3d11", arguments);
        }
        finally
        {
            Environment.SetEnvironmentVariable("JONDO_WINE_DESKTOP", before);
        }
    }

    [Fact]
    public void The_windows_client_on_linux_needs_wine_and_melonloader_before_a_slot_is_taken()
    {
        if (OperatingSystem.IsWindows()) return;
        var client = Client("Dofus.exe");
        client.Configure();
        File.WriteAllText(client.VersionFile, "Version=3.6.10.11\nConfigUrl=" + ClientInstallation.ConfigUrl);
        string? before = Environment.GetEnvironmentVariable("JONDO_WINE");
        string? prefix = Environment.GetEnvironmentVariable("JONDO_WINEPREFIX");
        try
        {
            Environment.SetEnvironmentVariable("JONDO_WINEPREFIX", Path.Combine(_root, "prefix", "wine"));
            Environment.SetEnvironmentVariable("JONDO_WINE", Path.Combine(_root, "no wine here"));
            Assert.Contains("needs Wine", Assert.Throws<InvalidOperationException>(client.ValidateNativeLaunch).Message);

            string wine = Path.Combine(_root, "wine");
            File.WriteAllText(wine, "");
            Environment.SetEnvironmentVariable("JONDO_WINE", wine);
            Assert.Contains("MelonLoader is not installed", Assert.Throws<InvalidOperationException>(client.ValidateNativeLaunch).Message);

            File.WriteAllText(Path.Combine(client.Root, "version.dll"), "");
            Directory.CreateDirectory(Path.Combine(client.Root, "MelonLoader"));
            client.ValidateNativeLaunch();
            Assert.True(Directory.Exists(Path.Combine(_root, "prefix")));

            File.WriteAllText(client.VersionFile, "Version=3.6.11.12\nConfigUrl=" + ClientInstallation.ConfigUrl);
            Assert.Contains("Unsupported client version", Assert.Throws<InvalidOperationException>(client.ValidateNativeLaunch).Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("JONDO_WINE", before);
            Environment.SetEnvironmentVariable("JONDO_WINEPREFIX", prefix);
        }
    }

    [Fact]
    public void Native_texture_packs_use_linux_manifests()
    {
        var client = Client();
        var service = TexturePackService.ForClient(client.Executable);
        Assert.Equal(PackState.Missing, service.Status(TexturePack.Hd).State);
        Assert.Contains("/linux/6.0_3.6.10.10.manifest", service.ManifestUrl);
    }

    [Fact]
    public void Discovery_uses_the_default_folder_after_explicit_and_versioned_installations()
    {
        string Create(string directory)
        {
            Directory.CreateDirectory(directory);
            string executable = Path.Combine(directory, ClientInstallation.ExecutableName);
            File.WriteAllText(executable, "");
            return executable;
        }

        string fallback = Path.Combine(_root, "DofusClient");
        string defaultExecutable = Create(fallback);
        string[] parents = { _root };
        Assert.Equal(defaultExecutable, ClientInstallation.Find(null, parents, fallback)!.Executable);
        Assert.Equal(defaultExecutable, ClientInstallation.Find("missing-client", parents, fallback)!.Executable);

        string older = Create(Path.Combine(_root, "Cliente 3.6.10.10"));
#if JONDO_PROTOCOL_3_8_1_2
        string newer = Create(Path.Combine(_root, "Cliente 3.8.1.2"));
#else
        string newer = Create(Path.Combine(_root, "Cliente 3.6.10.11"));
#endif
        Assert.Equal(newer, ClientInstallation.Find(null, parents, fallback)!.Executable);
        Assert.Equal(older, ClientInstallation.Find(older, parents, fallback)!.Executable);

        File.Delete(newer);
#if JONDO_PROTOCOL_3_8_1_2
        Assert.Equal(defaultExecutable, ClientInstallation.Find(null, parents, fallback)!.Executable);
#else
        Assert.Equal(older, ClientInstallation.Find(null, parents, fallback)!.Executable);
#endif
        File.Delete(older);
        File.Delete(defaultExecutable);
        Assert.Null(ClientInstallation.Find(null, parents, fallback));
    }

    [Fact]
    public void Provider_keys_round_trip_separately_and_are_not_written_in_clear()
    {
        string path = Path.Combine(_root, "deobfuscator.cfg");
        var settings = new Settings(path);
        settings.Use(Provider.All[0]); settings.Key = "first-provider-secret";
        settings.Use(Provider.All[1]); settings.Key = "second-provider-secret";
        settings.Save();
        Assert.DoesNotContain("provider-secret", File.ReadAllText(path));
        var loaded = Settings.Load(path);
        Assert.Equal("second-provider-secret", loaded.Key);
        loaded.Use(Provider.All[0]); Assert.Equal("first-provider-secret", loaded.Key);
    }

    [Fact]
    public void Mapping_cancellation_is_observed_before_reading_assemblies()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new Mapper().Build("missing-old", "missing-new", _root,
            Array.Empty<string>(), cancellationToken: cancellation.Token));
    }
}
