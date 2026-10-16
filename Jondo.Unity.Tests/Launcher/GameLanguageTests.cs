using System;
using System.IO;
using Jondo.Unity.Launcher.UI;
using Xunit;

namespace Jondo.Unity.Tests.Launcher
{
    /// <summary>
    /// The game's language dropdown: the packs are read off the client's I18n folder, and a choice
    /// the client does not have never reaches --langCode.
    /// </summary>
    /// <remarks>
    /// Every client is a temporary folder with an empty Dofus.exe and empty packs: only the file
    /// names are looked at.
    /// </remarks>
    public sealed class GameLanguageTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "jondo-langs-" + Guid.NewGuid().ToString("N"));

        private string Client => Path.Combine(_root, "Dofus.exe");

        public GameLanguageTests()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllBytes(Client, Array.Empty<byte>());
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private void Packs(params string[] names)
        {
            string folder = GameLanguages.PacksFolder(Client);
            Directory.CreateDirectory(folder);
            foreach (string name in names) File.WriteAllBytes(Path.Combine(folder, name), Array.Empty<byte>());
        }

        [Fact]
        public void The_packs_on_disk_are_listed_known_ones_first()
        {
            Packs("pt.bin", "it.bin", "de.bin", "en.bin", "es.bin", "fr.bin", "notes.txt", "bad name.bin");

            Assert.Equal(new[] { "es", "en", "fr", "de", "pt", "it" }, GameLanguages.Available(Client));
        }

        [Fact]
        public void Without_a_client_the_five_of_3_6_are_offered()
        {
            Assert.Equal(new[] { "es", "en", "fr", "de", "pt" }, GameLanguages.Available(""));
            Assert.Equal(new[] { "es", "en", "fr", "de", "pt" }, GameLanguages.Available(Client));
        }

        [Fact]
        public void An_empty_choice_follows_the_launcher()
        {
            Packs("es.bin", "en.bin", "fr.bin", "de.bin", "pt.bin");

            Assert.Equal("fr", GameLanguages.ForLaunch(Language.Fr, "", Client));
            Assert.Equal("de", GameLanguages.ForLaunch(Language.Fr, "de", Client));
        }

        [Fact]
        public void A_pack_the_client_lacks_falls_back_to_the_launcher()
        {
            Packs("es.bin", "en.bin", "fr.bin");

            Assert.Equal("en", GameLanguages.ForLaunch(Language.En, "pt", Client));
        }

        [Fact]
        public void Unknown_codes_are_named_by_their_code()
        {
            Assert.Equal("Deutsch", GameLanguages.NameOf("de"));
            Assert.Equal("Português", GameLanguages.NameOf("pt"));
            Assert.Equal("IT", GameLanguages.NameOf("it"));
        }
    }
}
