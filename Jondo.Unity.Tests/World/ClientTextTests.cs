using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Client;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Reading the client's own text table: every name and every line of dialogue in the game.
    /// </summary>
    /// <remarks>
    /// The format was worked out rather than documented, so it is worth pinning down. It was
    /// checked once against <c>world.db</c> — 500 keys sampled at random came back byte for byte
    /// identical, including a 42,180-character one — and these keep it that way.
    /// </remarks>
    public class ClientTextTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(),
                                                     "jondo-i18n-" + Guid.NewGuid().ToString("N") + ".bin");

        public void Dispose()
        {
            try { File.Delete(_path); } catch (IOException) { }
        }

        /// <summary>Writes a table in the client's own shape, so the reader is tested against it.</summary>
        private void Write(params (int Key, string Text)[] entries)
        {
            var strings = new MemoryStream();
            var offsets = new List<int>();

            int start = 7 + entries.Length * 8;
            foreach (var (_, text) in entries)
            {
                offsets.Add(start + (int)strings.Length);

                byte[] bytes = Encoding.UTF8.GetBytes(text);
                int length = bytes.Length;
                while (true)
                {
                    byte b = (byte)(length & 0x7F);
                    length >>= 7;
                    if (length != 0) b |= 0x80;
                    strings.WriteByte(b);
                    if (length == 0) break;
                }

                strings.Write(bytes, 0, bytes.Length);
            }

            using var file = new BinaryWriter(File.Create(_path));
            file.Write((byte)2);
            file.Write((byte)'e');
            file.Write((byte)'s');
            file.Write(entries.Length);

            for (int i = 0; i < entries.Length; i++)
            {
                file.Write(entries[i].Key);
                file.Write(offsets[i]);
            }

            file.Write(strings.ToArray());
        }

        [Fact]
        public void A_key_comes_back_with_its_text()
        {
            Write((1, "Comercio de cuenta o de abono"), (2, "Otra cosa"), (25, "Y una tercera"));

            var table = ClientText.Open(_path, GameLanguage.Spanish);

            Assert.NotNull(table);
            Assert.Equal(3, table!.Count);
            Assert.Equal("Comercio de cuenta o de abono", table.Of(1));
            Assert.Equal("Y una tercera", table.Of(25));
        }

        /// <summary>
        /// The index is sorted, which is what lets a key be found by bisection instead of by
        /// building a dictionary of 339,342 entries every time a language is picked.
        /// </summary>
        [Fact]
        public void A_key_in_the_middle_of_a_big_table_is_found()
        {
            var entries = new List<(int, string)>();
            for (int i = 1; i <= 5000; i++) entries.Add((i * 3, "text " + i));

            Write(entries.ToArray());
            var table = ClientText.Open(_path, GameLanguage.Spanish);

            Assert.Equal("text 2500", table!.Of(7500));
            Assert.Equal("text 1", table.Of(3));
            Assert.Equal("text 5000", table.Of(15000));
        }

        [Fact]
        public void A_key_that_is_not_there_comes_back_empty()
        {
            Write((1, "uno"), (5, "cinco"));

            var table = ClientText.Open(_path, GameLanguage.Spanish);

            Assert.Equal(ClientText.Missing, table!.Of(3));
            Assert.Equal(ClientText.Missing, table.Of(0));
            Assert.Equal(ClientText.Missing, table.Of(-4));
            Assert.False(table.Has(3));
            Assert.True(table.Has(5));
        }

        /// <summary>
        /// The length is a varint, so anything past 127 characters needs a second byte. The longest
        /// text in the real table is 42,180 characters, which needs three.
        /// </summary>
        [Fact]
        public void A_long_text_reads_whole()
        {
            string longOne = new string('x', 42_180);
            Write((1, "corto"), (2, longOne), (3, "otro corto"));

            var table = ClientText.Open(_path, GameLanguage.Spanish);

            Assert.Equal(42_180, table!.Of(2).Length);
            Assert.Equal("otro corto", table.Of(3));
        }

        [Fact]
        public void Accents_survive()
        {
            Write((1, "La montaña de los crujidores"), (2, "Rincón de los Jalatós"));

            var table = ClientText.Open(_path, GameLanguage.Spanish);

            Assert.Equal("La montaña de los crujidores", table!.Of(1));
            Assert.Equal("Rincón de los Jalatós", table.Of(2));
        }

        // ─── Not being there is a normal state ────────────────────────────────────

        /// <summary>
        /// The editor has to open on a machine with no client next to it, showing ids instead of
        /// names. A missing table says so and carries on.
        /// </summary>
        [Fact]
        public void A_missing_file_is_reported_and_not_thrown()
        {
            string complaint = "";
            var table = ClientText.Open(Path.Combine(Path.GetTempPath(), "no-such.bin"),
                                        GameLanguage.Spanish, message => complaint = message);

            Assert.Null(table);
            Assert.Contains("not there", complaint);
        }

        [Fact]
        public void A_file_that_is_not_one_of_these_is_reported_and_not_thrown()
        {
            File.WriteAllText(_path, "this is not a text table at all, not even close");

            string complaint = "";
            var table = ClientText.Open(_path, GameLanguage.Spanish, message => complaint = message);

            Assert.Null(table);
            Assert.NotEqual("", complaint);
        }

        [Fact]
        public void An_empty_file_is_survivable()
        {
            File.WriteAllBytes(_path, Array.Empty<byte>());

            Assert.Null(ClientText.Open(_path, GameLanguage.Spanish));
        }

        [Fact]
        public void The_language_tags_are_the_ones_the_client_uses()
        {
            Assert.Equal("es", ClientText.TagOf(GameLanguage.Spanish));
            Assert.Equal("en", ClientText.TagOf(GameLanguage.English));
            Assert.Equal("fr", ClientText.TagOf(GameLanguage.French));
        }

        // ─── Against the real client, when it is here ─────────────────────────────

        /// <summary>
        /// The three languages the emulator speaks, out of the client on this machine. Skipped when
        /// there is no client, which is a normal checkout.
        /// </summary>
        [Fact]
        public void The_real_tables_read_and_they_differ_from_each_other()
        {
            var read = new Dictionary<GameLanguage, ClientText>();
            foreach (var language in new[] { GameLanguage.Spanish, GameLanguage.English, GameLanguage.French })
            {
                var table = ClientText.Open(Paths.ClientTextFile(ClientText.TagOf(language)), language);
                if (table != null) read[language] = table;
            }

            if (read.Count == 0) return;

            foreach (var pair in read)
            {
                Assert.True(pair.Value.Count > 300_000,
                            $"{pair.Key} holds {pair.Value.Count} texts, which is too few to be the real table");
            }

            // Key 1 is "Trading in accounts or subscription" and its translations. If two languages
            // gave the same string the wrong file would be being read for one of them.
            if (read.Count > 1)
            {
                var texts = new HashSet<string>();
                foreach (var pair in read) texts.Add(pair.Value.Of(1));
                Assert.Equal(read.Count, texts.Count);
            }
        }
    }
}
