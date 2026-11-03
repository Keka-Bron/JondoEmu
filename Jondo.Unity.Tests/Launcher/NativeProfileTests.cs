using Jondo.Unity.Launcher;
using Jondo.Unity.Launcher.Security;
using Xunit;

namespace Jondo.Unity.Tests.Launcher;

[CollectionDefinition("Desktop profile environment", DisableParallelization = true)]
public sealed class DesktopProfileEnvironment { }

[Collection("Desktop profile environment")]
public sealed class NativeProfileTests
{
    [Fact]
    public void A_first_linux_launch_uses_absolute_xdg_paths_and_publishes_a_private_key()
    {
        if (!OperatingSystem.IsLinux()) return;
        string directory = Path.Combine(Path.GetTempPath(), "jondo-first-profile-" + Guid.NewGuid().ToString("N"));
        string? oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string? oldData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", directory);
            Assert.False(Directory.Exists(directory));
            Assert.Equal(directory, UserDirectories.Configuration);
            Assert.Equal(directory, UserDirectories.Data);
            Assert.Equal(Path.Combine(directory, "Jondo", "lanzador.cfg"), ClientInstallation.PreferencesFile);
            string encrypted = SecretStore.Protect("first-launch-secret");
            Assert.StartsWith("aesgcm:", encrypted);
            Assert.Equal("first-launch-secret", SecretStore.Unprotect(encrypted));
            string key = Path.Combine(directory, "Jondo", "clave.bin");
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(key));
            Assert.Equal(32, new FileInfo(key).Length);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldConfig);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldData);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
