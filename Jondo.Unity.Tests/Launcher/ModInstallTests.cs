using System;
using System.IO;
using Jondo.Unity.Launcher;
using Xunit;

namespace Jondo.Unity.Tests.Launcher
{
    /// <summary>
    /// The client's mods come with the emulator: the launcher puts the JondoFix and the Cazacabezas
    /// it ships into the client's Mods on its own, when they differ, and leaves a client without
    /// MelonLoader alone.
    /// </summary>
    public class ModInstallTests : IDisposable
    {
        private readonly string _client = Path.Combine(Path.GetTempPath(), $"jondo-cliente-{Guid.NewGuid():N}");
        private static string Shipped => Path.Combine(Paths.Root, "JondoFix", "JondoFix.dll");

        public ModInstallTests() => Directory.CreateDirectory(_client);

        public void Dispose()
        {
            try { Directory.Delete(_client, recursive: true); } catch (IOException) { }
        }

        [Fact]
        public void A_client_with_melonloader_gets_the_shipped_mod()
        {
            if (!File.Exists(Shipped)) return;
            Directory.CreateDirectory(Path.Combine(_client, "MelonLoader"));
            Directory.CreateDirectory(Path.Combine(_client, "Mods"));
            string installed = Path.Combine(_client, "Mods", "JondoFix.dll");
            File.WriteAllText(installed, "an old mod");

            LauncherService.InstallMod(Path.Combine(_client, "Dofus.exe"));

            Assert.True(LauncherService.SameContent(installed, Shipped));
        }

        [Fact]
        public void The_head_hunters_mod_goes_in_beside_it()
        {
            string shipped = Path.Combine(Paths.Root, "Cazacabezas", "Cazacabezas.dll");
            if (!File.Exists(shipped)) return;
            Directory.CreateDirectory(Path.Combine(_client, "MelonLoader"));

            LauncherService.InstallMod(Path.Combine(_client, "Dofus.exe"));

            Assert.True(LauncherService.SameContent(Path.Combine(_client, "Mods", "Cazacabezas.dll"), shipped));
        }

        [Fact]
        public void A_client_without_melonloader_is_left_alone()
        {
            if (!File.Exists(Shipped)) return;
            LauncherService.InstallMod(Path.Combine(_client, "Dofus.exe"));
            Assert.False(Directory.Exists(Path.Combine(_client, "Mods")));
        }
    }
}
