using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Protocol
{
    /// <summary>
    /// Field 5 of hoy, and why it is not written.
    /// </summary>
    /// <remarks>
    /// It was written, for one build, on a correlation that looked convincing and was not. Across
    /// the first six captures f5 showed up in exactly one of the two accounts recorded, and that
    /// was the account holding four characters on one server while the other never held more than
    /// one. Subscription marker, obviously. So a 2 went out on every hoy.
    ///
    /// The capture "crear personaje - borrar personaje" is the same account as the one that had
    /// f5, logging in three times, creating an eleventh character and deleting it, with the create
    /// button working throughout. Its hoy is
    ///
    /// <code>
    ///   081e100118013202667238c801      f1=30 f2=1 f3=1 f6="fr" f7=200, and no f5 at all
    /// </code>
    ///
    /// One account, f5 present in one session and absent in another, the button fine either way.
    /// It marks nothing about what the account may do, and copying a value nobody had decoded was
    /// the mistake -- not a harmless extra.
    ///
    /// These tests pin its absence so it does not come back on the same reasoning.
    /// </remarks>
    public class SubscriptionTierTests
    {
        /// <summary>The hoy payload out of the welcome burst, unwrapped from its envelope.</summary>
        private static byte[] Hoy()
        {
            const string Prefix = "type.ankama.com/hoy";

            foreach (byte[] frame in ConnectionProtocol.BuildWelcomeBurst(
                         new List<DatabaseManager.DbCharacter>()))
            {
                string ascii = System.Text.Encoding.ASCII.GetString(frame);
                int at = ascii.IndexOf(Prefix, StringComparison.Ordinal);
                if (at < 0) continue;

                // Straight after the type url comes field 2, the payload, with its length.
                int cursor = at + Prefix.Length;
                if (frame[cursor] != 0x12) continue;

                int length = frame[cursor + 1];
                return frame.Skip(cursor + 2).Take(length).ToArray();
            }

            return Array.Empty<byte>();
        }

        [Fact]
        public void The_frame_is_the_one_the_working_account_receives()
        {
            // Byte for byte the capture, with only the language changed: this server launches the
            // client in Spanish and the account recorded was French, so 6672 becomes 6573.
            Assert.Equal("081e100118013202657338c801",
                         Convert.ToHexString(Hoy()).ToLowerInvariant());
        }

        [Fact]
        public void Field_five_is_not_there()
        {
            // The revert, stated as a fact rather than as a comment. 2802 is field 5 with the
            // value 2, which is what went out for one build.
            Assert.DoesNotContain("2802", Convert.ToHexString(Hoy()).ToLowerInvariant());
        }

        [Fact]
        public void The_fields_that_are_there_are_the_measured_ones()
        {
            // Everything else in the frame is identical in every capture of both accounts, so it
            // is pinned individually: losing one of these while editing the frame would be quiet.
            string hex = Convert.ToHexString(Hoy()).ToLowerInvariant();

            Assert.StartsWith("081e", hex);          // f1 = 30
            Assert.Contains("1001", hex);            // f2 = 1
            Assert.Contains("1801", hex);            // f3 = 1
            Assert.Contains("32026573", hex);        // f6 = "es"
            Assert.EndsWith("38c801", hex);          // f7 = 200
        }

        [Fact]
        public void It_is_thirteen_bytes_like_the_capture()
        {
            // With f5 it was fifteen. The length alone catches the regression.
            Assert.Equal(13, Hoy().Length);
        }
    }
}
