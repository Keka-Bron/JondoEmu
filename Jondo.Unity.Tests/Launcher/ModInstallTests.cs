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
        public void The_heroes_mod_goes_in_too()
        {
            string shipped = Path.Combine(Paths.Root, "Heroes", "Heroes.dll");
            if (!File.Exists(shipped)) return;
            Directory.CreateDirectory(Path.Combine(_client, "MelonLoader"));

            LauncherService.InstallMod(Path.Combine(_client, "Dofus.exe"));

            Assert.True(LauncherService.SameContent(Path.Combine(_client, "Mods", "Heroes.dll"), shipped));
        }

        /// <summary>
        /// A client open holds its mod as a loaded file holds it: it can be renamed, not
        /// overwritten. The old one is set aside and the new one goes in its place.
        /// </summary>
        [Fact]
        public void A_mod_a_client_holds_is_set_aside_and_replaced()
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(Shipped)) return;
            Directory.CreateDirectory(Path.Combine(_client, "MelonLoader"));
            Directory.CreateDirectory(Path.Combine(_client, "Mods"));
            string installed = Path.Combine(_client, "Mods", "JondoFix.dll");
            File.WriteAllText(installed, "an old mod, loaded");

            using (new FileStream(installed, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                LauncherService.InstallMod(Path.Combine(_client, "Dofus.exe"));
                Assert.True(LauncherService.SameContent(installed, Shipped));
                Assert.True(File.Exists(installed + ".old"));
            }

            // The next launch, nobody holding it: the one set aside is gone.
            LauncherService.InstallMod(Path.Combine(_client, "Dofus.exe"));
            Assert.False(File.Exists(installed + ".old"));
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
