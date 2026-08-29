using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Protocol
{
    /// <summary>
    /// The frame that told every account it had reached its character limit.
    /// </summary>
    /// <remarks>
    /// "CREAR UN PERSONAJE" was grey with one character on the account, under the message "ya has
    /// alcanzado la cantidad máxima de personajes para tu cuenta". Nine explanations were measured
    /// and dropped first -- the extra kvd, the slot count, the year in the subscription date, the
    /// server category, a shop the client never calls, field 5 of hoy, the launcher's own copy of
    /// the date, five opcodes the real server does not answer either, and the distance of the date
    /// -- and each is pinned by its own tests. The burst was compared frame by frame against the
    /// captures and came out equal, twice, which is what kept sending the search elsewhere.
    ///
    /// It was not equal. mgq differs by two bytes, and sorting every capture in
    /// "Autenticacion-Servidor-Personaje" by whether a character could be created puts field 1
    /// cleanly on one side:
    ///
    /// <code>
    ///   10011801       creation succeeds          "creacion personaje-exito"
    ///   10011801       creation succeeds          "crear personaje - borrar personaje"
    ///   10011801       already in the world       "tutorial completo"
    ///   080110011801   refused, maximum reached   "fallo por limite maximo"
    ///   080110011801   account sitting at 4/5     "eleccion servidor a eleccion personaje"
    /// </code>
    ///
    /// One account sends it in one session and not in the next, so it is a state rather than a
    /// property of the account. This server sent it on every login.
    ///
    /// It also explains the one observation that fitted nothing else: an account with NO characters
    /// could still create its first. A limit of one is not a limit you have hit until you have one.
    ///
    /// The honest edge: what field 1 means is not decoded, only correlated. Two of the five rows
    /// are the same account and the tie between "is at the limit" and "sends field 1" rests on
    /// three captures where creation was actually attempted. It is pinned as bytes, which is what
    /// was measured, not as a meaning.
    /// </remarks>
    public class CharacterLimitFrameTests
    {
        private static byte[] Frame(string opcode)
        {
            string prefix = "type.ankama.com/" + opcode;

            foreach (byte[] frame in ConnectionProtocol.BuildWelcomeBurst(
                         new List<DatabaseManager.DbCharacter>()))
            {
                string ascii = System.Text.Encoding.ASCII.GetString(frame);
                int at = ascii.IndexOf(prefix, StringComparison.Ordinal);
                if (at < 0) continue;

                // A frame with no payload -- krs is one -- ends right after the type url, so the
                // bounds check is the answer for it rather than an error.
                int cursor = at + prefix.Length;
                if (cursor + 1 >= frame.Length || frame[cursor] != 0x12) return Array.Empty<byte>();

                return frame.Skip(cursor + 2).Take(frame[cursor + 1]).ToArray();
            }

            return Array.Empty<byte>();
        }

        private static string Hex(string opcode)
            => Convert.ToHexString(Frame(opcode)).ToLowerInvariant();

        [Fact]
        public void Mgq_is_the_shape_the_working_captures_carry()
        {
            // Byte for byte "creacion personaje-exito" and "crear personaje - borrar personaje".
            Assert.Equal("10011801", Hex("mgq"));
        }

        [Fact]
        public void Field_one_is_gone()
        {
            // Stated separately from the shape above, because this is the actual fix and it should
            // fail on its own terms if someone puts the field back.
            Assert.DoesNotContain("0801", Hex("mgq"));
            Assert.Equal(4, Frame("mgq").Length);
        }

        [Fact]
        public void The_two_fields_that_stay_are_still_there()
        {
            // f2 and f3, both 1, in every capture of both accounts without exception. Dropping one
            // of these while removing f1 would be an easy mistake and a silent one.
            Assert.Contains("1001", Hex("mgq"));
            Assert.Contains("1801", Hex("mgq"));
        }

        [Fact]
        public void Its_neighbours_in_the_burst_are_untouched()
        {
            // mgt, hpd and krs are identical in all seven captures and are asserted here so that
            // editing this corner of the burst cannot quietly disturb them.
            Assert.Equal("1200", Hex("mgt"));
            Assert.Equal("0801", Hex("hpd"));
            Assert.Equal("", Hex("krs"));
        }
    }
}
