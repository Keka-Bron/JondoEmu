using System;
using System.IO;
using Jondo.Unity.Launcher;
using Xunit;

namespace Jondo.Unity.Tests.Launcher;

public class ModInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"jondo-cliente-{Guid.NewGuid():N}");
    private string Client => Path.Combine(_root, "client");
    private string Bundle => Path.Combine(_root, "bundle");

    public ModInstallTests() => Directory.CreateDirectory(Client);

    [Fact]
    public void A_client_with_melonloader_gets_the_bundled_dlls()
    {
        const string existingName = "Existing.dll";
        const string extraName = "Extra.dll";
        string existing = BundleFile(existingName);
        string extra = BundleFile(extraName);
        BundleFile(ClientModManifest.BootstrapFileName);
        BundleFile("Not-a-mod.txt");
        Directory.CreateDirectory(Path.Combine(Client, "MelonLoader"));
        string mods = Directory.CreateDirectory(Path.Combine(Client, "Mods")).FullName;
        File.WriteAllText(Path.Combine(mods, existingName), "an old mod");

        Install();

        Assert.True(BundledClientMods.SameContent(Path.Combine(mods, existingName), existing));
        Assert.True(BundledClientMods.SameContent(Path.Combine(mods, extraName), extra));
        Assert.False(File.Exists(Path.Combine(mods, ClientModManifest.BootstrapFileName)));
        Assert.False(File.Exists(Path.Combine(mods, "Not-a-mod.txt")));
    }

    [Fact]
    public void A_mod_a_client_holds_is_set_aside_and_replaced()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string name = "Loaded.dll";
        string shipped = BundleFile(name);
        Directory.CreateDirectory(Path.Combine(Client, "MelonLoader"));
        string mods = Directory.CreateDirectory(Path.Combine(Client, "Mods")).FullName;
        string installed = Path.Combine(mods, name);
        File.WriteAllText(installed, "an old mod, loaded");

        using (new FileStream(installed, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            Install();
            Assert.True(BundledClientMods.SameContent(installed, shipped));
            Assert.True(File.Exists(installed + ".old"));
        }

        Install();
        Assert.False(File.Exists(installed + ".old"));
    }

    [Fact]
    public void A_client_without_melonloader_is_left_alone()
    {
        BundleFile("Extra.dll");
        Install();
        Assert.False(Directory.Exists(Path.Combine(Client, "Mods")));
    }

    private string BundleFile(string filename)
    {
        string mods = Directory.CreateDirectory(Path.Combine(Bundle, "mods")).FullName;
        string path = Path.Combine(mods, filename);
        File.WriteAllText(path, "bundled client mod");
        return path;
    }

    private void Install() => LauncherService.InstallMod(Path.Combine(Client, "Dofus.exe"), Bundle);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
