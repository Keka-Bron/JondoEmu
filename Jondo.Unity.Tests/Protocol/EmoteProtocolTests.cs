using System;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Emotes;
using Xunit;

namespace Jondo.Unity.Tests.Protocol
{
    /// <summary>
    /// Emotes and smileys, byte for byte against the captures of the <c>Emotes</c> folder.
    /// </summary>
    public class EmoteProtocolTests
    {
        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

        private static byte[] Bytes(string hex) => Convert.FromHexString(hex);

        [Fact]
        public void The_juggler_is_the_captured_khh()
        {
            // Emotes\usar emote malabares-cancelarlo luego movimiendome, frames 4 and 5.
            Assert.Equal(29, EmoteProtocol.ReadPlay(Bytes("081d")));
            Assert.Equal("08a28280c8e708181d28a2dab71f320f416e696d456d6f74654a7567676c65",
                Hex(EmoteProtocol.BuildPlayed(302677754146, 29, 65924386, "AnimEmoteJuggle")));
        }

        [Fact]
        public void Sitting_is_an_emote_like_the_others()
        {
            // Emotes\captura de muchas actitudes, frames 3 and 4: khl {1} and khh "AnimEmoteSit".
            Assert.Equal(1, EmoteProtocol.ReadPlay(Bytes("0801")));
            Assert.Equal("08a28280c8e7081801" + "28a2dab71f" + "320c" + "416e696d456d6f7465536974",
                Hex(EmoteProtocol.BuildPlayed(302677754146, 1, 65924386, "AnimEmoteSit")));
        }

        [Fact]
        public void A_new_character_s_list_is_the_captured_khn()
        {
            // Autenticacion-Servidor-Personaje\crear personaje - borrar personaje, frame 57.
            Assert.Equal("0a040161627f", Hex(EmoteProtocol.BuildList(EmoteRules.Starting)));
        }

        [Fact]
        public void An_emote_learned_is_the_captured_khi()
        {
            // Gremio\comprar gremialogema-crear gremio: khi {97}, the guild banner, on founding.
            Assert.Equal("0861", Hex(EmoteProtocol.BuildLearned(97)));
        }

        [Fact]
        public void A_smiley_is_the_captured_hoc()
        {
            // Emotes\usar emoticonos-poner estado animo actual-dejarlo en blanco, frames 4 and 5.
            Assert.Equal(1, EmoteProtocol.ReadSmiley(Bytes("1001")));
            Assert.Equal("10a28280c8e70818a2dab71f2001", Hex(EmoteProtocol.BuildSmiley(302677754146, 65924386, 1)));
            Assert.Equal("10a28280c8e70818a2dab71f2063", Hex(EmoteProtocol.BuildSmiley(302677754146, 65924386, 99)));
        }

        [Fact]
        public void The_mood_is_set_and_cleared_as_captured()
        {
            // Frames 56-57: hor {5: 40} and hns {3: 40}; frames 71-72: both empty.
            Assert.Equal(40, EmoteProtocol.ReadMood(Bytes("2828")));
            Assert.Equal("1828", Hex(EmoteProtocol.BuildMood(40)));
            Assert.Equal(0, EmoteProtocol.ReadMood(Array.Empty<byte>()));
            Assert.Equal("", Hex(EmoteProtocol.BuildMood(0)));
        }
    }
}
