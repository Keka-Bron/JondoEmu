using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Sessions
{
    /// <summary>
    /// Deleting a character, and the frame set that tells the client the list has changed.
    /// </summary>
    /// <remarks>
    /// Both halves come out of "crear personaje - borrar personaje.pcapng", which records one
    /// creation and one deletion by the real client against the real server.
    ///
    /// The list half is the one that had already caused a visible bug. After a creation this
    /// server sent the new list as a bare kvi, and the client kept the list it had: press play
    /// straight after making a character and the PREVIOUS one walked into the world, because the
    /// selection the client sent named the only character it still knew about. Nothing was wrong
    /// with the lookup -- the id that arrived was honoured correctly. Going back to the selection
    /// screen refreshed everything, which is what a stale list looks like from outside.
    ///
    /// In the capture the list never travels alone. It is three kqp, the kvi, and the gift
    /// catalogue, in the welcome burst and after a creation and after a deletion alike.
    ///
    /// The deletion itself is three requests and three answers, and hanging all three answers off
    /// the kvu -- which is how it was written first -- makes the bin button do nothing at all: the
    /// confirmation popup never opens because it is waiting for the kvn that answers the kwa.
    ///
    /// <code>
    ///   kwa  -&gt;  kvn                    the name, and the popup opens
    ///   kvu  -&gt;  kqp kqp kqp  kvi  jtg  deleted, and here is the list again
    ///   kvh  -&gt;  kvm                    closed
    /// </code>
    /// </remarks>
    public class CharacterDeletionTests
    {
        private static readonly List<DatabaseManager.DbCharacter> Two = new()
        {
            new DatabaseManager.DbCharacter { Id = 100_001, Name = "Primero", Level = 1, Breed = 13 },
            new DatabaseManager.DbCharacter { Id = 100_002, Name = "Segundo", Level = 1, Breed = 4 },
        };

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

        // ------------------------------------------------------------------ the list travels whole

        [Fact]
        public void The_list_is_five_frames_in_the_captured_order()
        {
            Assert.Equal(new[] { Op.Kqp, Op.Kqp, Op.Kqp, Op.Kvi, Op.Jtg },
                         Opcodes(ConnectionProtocol.CharacterListFrames(Two)).ToArray());
        }

        [Fact]
        public void The_welcome_burst_still_ends_with_it()
        {
            // Factoring the frames out must not have moved them: the burst finishes with the list,
            // and WelcomeBurstTests pins what comes before.
            var burst = Opcodes(ConnectionProtocol.BuildWelcomeBurst(Two));

            Assert.Equal(new[] { Op.Kqp, Op.Kqp, Op.Kqp, Op.Kvi, Op.Jtg },
                         burst.Skip(burst.Count - 5).ToArray());
        }

        [Fact]
        public void An_empty_account_gets_the_same_five()
        {
            // The state a deletion can leave behind. The frames are the same; only the kvi is bare.
            Assert.Equal(5, ConnectionProtocol.CharacterListFrames(
                new List<DatabaseManager.DbCharacter>()).Count);
        }

        // ------------------------------------------------------------------------- reading the id

        [Fact]
        public void The_id_is_read_from_field_one_of_the_kvu()
        {
            // 71434240350 is the id in the capture, carried as de82c08e8a02.
            byte[] frame = ConnectionProtocol.Push(
                Op.Kvu, Pb.New().Var(1, 71_434_240_350L).Str(2, new string('a', 32)).Build());

            Assert.Equal(71_434_240_350L, CharacterDeletionHandler.ReadCharacterId(frame));
        }

        [Fact]
        public void And_from_field_two_of_the_kwa_that_comes_first()
        {
            // The client sends the kwa before the kvu, with the same id in a different field.
            // Reading both means a deletion is not lost if it only gets that far.
            byte[] frame = ConnectionProtocol.Push(Op.Kwa, Pb.New().Var(2, 100_002L).Build());

            Assert.Equal(100_002L, CharacterDeletionHandler.ReadCharacterId(frame));
        }

        [Fact]
        public void A_frame_with_no_id_reads_as_nothing()
        {
            // Zero has to mean "no id" rather than "the first character", which is the shape of
            // bug this whole area has produced before.
            Assert.Equal(0, CharacterDeletionHandler.ReadCharacterId(ConnectionProtocol.Push(Op.Kvu)));
            Assert.Equal(0, CharacterDeletionHandler.ReadCharacterId(ConnectionProtocol.Push(Op.Kvh)));
            Assert.Equal(0, CharacterDeletionHandler.ReadCharacterId(Array.Empty<byte>()));
        }

        [Fact]
        public void Another_message_carrying_a_number_is_not_mistaken_for_a_deletion()
        {
            // kvw is the selection and also carries an id in field 1. Reading it here would delete
            // whatever the player just chose to play.
            byte[] selection = ConnectionProtocol.Push(Op.Kvw, Pb.New().Var(1, 100_002L).Build());

            Assert.Equal(0, CharacterDeletionHandler.ReadCharacterId(selection));
        }

        // ------------------------------------------------------------------------- what protects it

        [Fact]
        public void Deleting_needs_both_an_id_and_an_account()
        {
            // The database refuses before it looks anything up. A socket that has not presented its
            // ticket arrives with account zero, and zero must delete nothing.
            Assert.Equal("", DatabaseManager.DeleteCharacter(100_002L, 0));
            Assert.Equal("", DatabaseManager.DeleteCharacter(0, 100_000_001L));
            Assert.Equal("", DatabaseManager.DeleteCharacter(-1, -1));
        }
    }
}
