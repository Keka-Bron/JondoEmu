using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Protocol
{
    /// <summary>
    /// The burst that builds the character-selection screen, and the one frame that broke it.
    /// </summary>
    /// <remarks>
    /// Two things were dead on that screen: "create a character" would not light up, and "change
    /// server" led nowhere. One extra frame explains both.
    ///
    /// <c>kvd</c> was added on a guess — that it "closes the character list", and that the button
    /// was dark because the screen never got its ending. It reads well and it is backwards.
    /// Measured across every capture in the repository, <c>kvd</c> appears in exactly three, and
    /// all three are the client going straight into the world without stopping at the screen:
    ///
    /// <code>
    ///   kvi(381)  kvd(0)  ipc  kva  mft        reconnecting into a fight
    ///   kra  kqu  kvd(0)  kva  ivx  hlm        koliseo — there is not even a kvi
    /// </code>
    ///
    /// And in the three captures that DO show the character screen — the account with four
    /// characters and the button active, the empty account that creates one, and the one that
    /// fails on the maximum — it is not there at all. All three go <c>kvi</c> then <c>jtg</c>.
    ///
    /// So it means "do not stop here". Sending it always made the client build the screen as a
    /// pass-through.
    /// </remarks>
    public class WelcomeBurstTests
    {
        /// <summary>
        /// The three letters of each frame, read out of the bytes.
        /// </summary>
        /// <remarks>
        /// Scanned rather than parsed on purpose: the readers in NetworkEnvelope each expect one
        /// root field number — 1 for what the client sends, 3 for what the server answers — and
        /// asking the wrong one gets a silent null. The type url is plain ASCII inside the frame
        /// either way, so looking for it works whichever envelope Push happens to build.
        /// </remarks>
        private static List<string> Opcodes(IEnumerable<byte[]> frames)
        {
            const string Prefix = "type.ankama.com/";
            var found = new List<string>();

            foreach (byte[] frame in frames)
            {
                string ascii = System.Text.Encoding.ASCII.GetString(frame);
                int at = ascii.IndexOf(Prefix, StringComparison.Ordinal);
                if (at < 0 || at + Prefix.Length + 3 > ascii.Length) continue;

                found.Add(ascii.Substring(at + Prefix.Length, 3));
            }

            return found;
        }

        private static List<string> Burst(int characters)
        {
            var list = new List<DatabaseManager.DbCharacter>();
            for (int i = 0; i < characters; i++)
            {
                list.Add(new DatabaseManager.DbCharacter
                {
                    Id = 1000 + i,
                    Name = "Prueba" + i,
                    Level = 1,
                    Breed = 1,
                });
            }

            return Opcodes(ConnectionProtocol.BuildWelcomeBurst(list));
        }

        [Fact]
        public void The_character_screen_burst_carries_no_kvd()
        {
            // The whole bug in one assertion.
            Assert.DoesNotContain(Op.Kvd, Burst(1));
            Assert.DoesNotContain(Op.Kvd, Burst(0));
            Assert.DoesNotContain(Op.Kvd, Burst(4));
        }

        [Fact]
        public void The_gift_catalogue_comes_straight_after_the_list()
        {
            // What the real burst does, and what having kvd in between broke: kvi then jtg, with
            // nothing between them. Asserted as adjacency rather than as "kvd is absent" so that
            // putting anything else in that gap fails here too.
            var burst = Burst(2);
            int list = burst.IndexOf(Op.Kvi);

            Assert.True(list >= 0, "la ráfaga no lleva la lista de personajes");
            Assert.Equal(Op.Jtg, burst[list + 1]);
        }

        [Fact]
        public void The_order_is_the_one_the_capture_has()
        {
            // Frames 1 to 14 of the S->C stream of
            // "eleccion servidor vacio-creacion personaje-exito...", in order. Our burst matches
            // it exactly once the kvd is gone -- the two kqz our log shows before this belong to
            // the handshake and are not part of it.
            string[] measured =
            {
                Op.Kra, Op.Lqu, Op.Hoy, Op.Kqu, Op.Mgq, Op.Mgt, Op.Hpd, Op.Krs, Op.Mgz,
                Op.Kqp, Op.Kqp, Op.Kqp, Op.Kvi, Op.Jtg,
            };

            Assert.Equal(measured, Burst(1).ToArray());
        }

        [Fact]
        public void An_account_with_no_characters_gets_the_same_frames()
        {
            // The empty account is the one that most needs the create button, and its burst has
            // the same shape in the capture — only the kvi is empty.
            Assert.Equal(Burst(1).Count, Burst(0).Count);
        }
    }
}
