using Jondo.Unity.Deobfuscator;
using Jondo.Unity.Launcher;
using Jondo.Unity.Launcher.Packs;
using Jondo.Unity.Reversing;
using Xunit;

namespace Jondo.Unity.Tests.Launcher;

public sealed class NativeClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jondo native client é " + Guid.NewGuid().ToString("N"));
    public NativeClientTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ClientInstallation Client(string name = "Dofus.x64")
    {
        string executable = Path.Combine(_root, name);
        File.WriteAllText(executable, "");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var client = new ClientInstallation(executable);
        Directory.CreateDirectory(Path.GetDirectoryName(client.VersionFile)!);
        File.WriteAllText(client.VersionFile, "Version=3.6.10.10\nBuildDate=original\nConfigUrl=https://original\nOther=keep\n");
        return client;
    }

    [Fact]
    public void Configuring_preserves_unrelated_settings_and_an_original_backup()
    {
        var client = Client();
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
        File.WriteAllText(client.VersionFile, "Version=3.7.10.10\nConfigUrl=" + ClientInstallation.ConfigUrl);
        Assert.Contains("Unsupported client version", Assert.Throws<InvalidOperationException>(client.ValidateNativeLaunch).Message);
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(client.Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Contains("chmod +x", Assert.Throws<InvalidOperationException>(client.ValidateNativeLaunch).Message);
    }

    [Fact]
    public void Native_launch_arguments_preserve_paths_and_credentials_without_windows_flags()
    {
        var client = Client();
        var start = LauncherService.ClientStartInfo(client.Executable, 1280, 720, 17,
            "hash with spaces", "fr", "--hdReady", Path.Combine(_root, "shared preferences"), Path.Combine(_root, "logs"));
        Assert.False(start.UseShellExecute);
        Assert.Equal(client.Root, start.WorkingDirectory);
        Assert.Equal("hash with spaces", start.Environment["ZAAP_HASH"]);
        Assert.Equal("15881", start.Environment["ZAAP_PORT"]);
        Assert.Contains("hash with spaces", start.ArgumentList);
        Assert.DoesNotContain("-force-d3d11", start.ArgumentList);
        Assert.DoesNotContain(start.ArgumentList, arg => arg.Contains("melonloader"));
        Assert.Contains("--hdReady", start.ArgumentList);
        Assert.True(Directory.Exists(start.Environment["XDG_CONFIG_HOME"]));
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
        string newer = Create(Path.Combine(_root, "Cliente 3.6.10.11"));
        Assert.Equal(newer, ClientInstallation.Find(null, parents, fallback)!.Executable);
        Assert.Equal(older, ClientInstallation.Find(older, parents, fallback)!.Executable);

        File.Delete(newer);
        Assert.Equal(older, ClientInstallation.Find(null, parents, fallback)!.Executable);
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
