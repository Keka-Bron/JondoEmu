using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
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

        private static async Task<IReadOnlyList<GameFrame>> CaptureAsync(Func<GameSession, Task> send)
        {
            var codec = new RecordingCodec();
            var session = new GameSession(codec, _ => Task.CompletedTask);
            session.BindAccount(8_919_099_002, 290);
            await send(session);
            return codec.Frames;
        }

        private static Task<IReadOnlyList<GameFrame>> RefreshAsync(IReadOnlyList<DatabaseManager.DbCharacter> characters)
            => CaptureAsync(session => new CharacterListService(
                new(_ => new([], 3, 1, [], [], [])), new(_ => { }, _ => [], () => [], _ => { }))
                .SendAsync(session, characters));

        private static void AssertRefresh(IReadOnlyList<GameFrame> refresh, params long[] characterIds)
        {
            Assert.Equal(5, refresh.Count);
            Assert.Equal(new GamePayload[] { new QueueStatusEvent(1, 1), new QueueStatusEvent(1, 0),
                new QueueStatusEvent(0, 0) }, refresh.Take(3).Select(frame => frame.Content));
            var list = Assert.IsType<CharacterListEvent>(refresh[3].Content);
            Assert.Equal(characterIds, list.Characters.Select(character => character.Id));
            Assert.IsType<GameActionItemListEvent>(refresh[4].Content);
            Assert.All(refresh, frame => Assert.Null(frame.Uid));
        }

        // ------------------------------------------------------------------ semantic list delivery

        [Fact]
        public async Task The_refresh_sends_three_queue_events_then_the_list_and_gifts()
        {
            AssertRefresh(await RefreshAsync(Two), 100_001, 100_002);
        }

        [Fact]
        public async Task The_production_welcome_ends_with_the_same_semantic_refresh_without_force_selection()
        {
            var frames = await CaptureAsync(session => ConnectionProtocol.SendWelcomeAsync(session, Two, withList: true));
            Assert.Contains(frames, frame => frame.Content is ServerSessionReadyEvent);
            Assert.DoesNotContain(frames, frame => frame.Content is CharacterForceSelectionEvent);
            AssertRefresh(frames.TakeLast(5).ToArray(), 100_001, 100_002);
        }

        [Fact]
        public async Task An_empty_account_receives_an_empty_replacement_in_the_same_sequence()
        {
            AssertRefresh(await RefreshAsync([]));
        }

        // ------------------------------------------------------------------------- reading the id

        [Fact]
        public void The_id_is_read_from_field_one_of_the_kvu()
        {
            // 71000000018, carried as 928cb8bf8802.
            byte[] frame = ConnectionProtocol.Push(
                Op.Kvu, Pb.New().Var(1, 71_000_000_018L).Str(2, new string('a', 32)).Build());

            Assert.Equal(71_000_000_018L, CharacterDeletionHandler.ReadCharacterId(frame));
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

        private sealed class RecordingCodec : IGameCodec
        {
            internal List<GameFrame> Frames { get; } = [];
            public byte[] Encode(GameFrame frame) { Frames.Add(frame); return [0x42]; }
            public GameFrame Decode(ReadOnlySpan<byte> payload) => throw new Exception("Unexpected list decode");
        }
    }
}
