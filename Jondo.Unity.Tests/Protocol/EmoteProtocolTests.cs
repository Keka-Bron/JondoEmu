using System;
using Jondo.Unity.Server.Network;
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
            Assert.Equal("0881f092cbdd08181d28818ece1c320f416e696d456d6f74654a7567676c65",
                Hex(EmoteProtocol.BuildPlayed(300000000001, 29, 60000001, "AnimEmoteJuggle")));
        }

        [Fact]
        public void Sitting_is_an_emote_like_the_others()
        {
            // Emotes\captura de muchas actitudes, frames 3 and 4: khl {1} and khh "AnimEmoteSit".
            Assert.Equal(1, EmoteProtocol.ReadPlay(Bytes("0801")));
            Assert.Equal("0881f092cbdd081801" + "28818ece1c" + "320c" + "416e696d456d6f7465536974",
                Hex(EmoteProtocol.BuildPlayed(300000000001, 1, 60000001, "AnimEmoteSit")));
        }

        [Fact]
        public void A_smiley_is_the_captured_hoc()
        {
            // Emotes\usar emoticonos-poner estado animo actual-dejarlo en blanco, frames 4 and 5.
            Assert.Equal(1, EmoteProtocol.ReadSmiley(Bytes("1001")));
            Assert.Equal("1081f092cbdd0818818ece1c2001", Hex(EmoteProtocol.BuildSmiley(300000000001, 60000001, 1)));
            Assert.Equal("1081f092cbdd0818818ece1c2063", Hex(EmoteProtocol.BuildSmiley(300000000001, 60000001, 99)));
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
