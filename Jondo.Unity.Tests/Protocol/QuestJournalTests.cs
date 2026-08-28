using System;
using System.Collections.Generic;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Protocol
{
    /// <summary>
    /// The journal message (idr), against the bytes the real server sent.
    /// </summary>
    /// <remarks>
    /// This is the message that used to hand every player somebody else's quest log. The captured
    /// world-entry block carries 261 <c>idu</c> frames AND one <c>idr</c> holding the same 261
    /// inside it, so taking only the <c>idu</c> out of the replay changed nothing anybody could
    /// see: the client still opened on 261 quests in progress and 622 finished, none of them the
    /// player's, which is also why no NPC carried a green mark — every quest already looked done.
    ///
    /// The counts are the proof that the shape is read right, not guessed: 261 entries in f1, 548
    /// in f3 and 74 in the packed f4, and the client prints "En proceso (261)" and
    /// "Terminadas (622)". 548 + 74 = 622.
    /// </remarks>
    public class QuestJournalTests
    {
        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

        [Fact]
        public void One_quest_under_way_matches_the_captured_entry()
        {
            // First entry of the captured idr: quest 1536 on step 2156, objective 8863 outstanding.
            //   f1 { f2 { f1 { f2: 8863, f4: 1 }, f2: 2156 }, f3: 1536 }
            var journal = QuestProtocol.BuildJournal(
                new[] { (1536, 2156, (IReadOnlyList<int>)new[] { 8863 },
                         (IReadOnlyCollection<int>)Array.Empty<int>()) },
                Array.Empty<int>());

            // The entry itself, byte for byte as the capture carries it.
            Assert.Contains("120a0a05109f45200110ec1018800c", Hex(journal));
        }

        [Fact]
        public void A_finished_quest_is_a_pair_and_not_a_bare_id()
        {
            // Captured: 0801108010 / 0801108110 / 0801108210 for quests 2048, 2049 and 2050.
            // The f1 is a constant 1 in all 548 of them. Writing the id alone would be a shorter
            // message the client cannot read.
            string hex = Hex(QuestProtocol.BuildJournal(
                Array.Empty<(int, int, IReadOnlyList<int>, IReadOnlyCollection<int>)>(),
                new[] { 2048, 2049, 2050 }));

            Assert.Contains("1a050801108010", hex);
            Assert.Contains("1a050801108110", hex);
            Assert.Contains("1a050801108210", hex);
        }

        [Fact]
        public void A_finished_objective_drops_its_flag_here_too()
        {
            // Same rule as idu, and worth pinning separately because this message is built by
            // different code: f4 means STILL TO DO. An objective that is done carries no f4 at all.
            string done = Hex(QuestProtocol.BuildJournal(
                new[] { (1536, 2156, (IReadOnlyList<int>)new[] { 8863 },
                         (IReadOnlyCollection<int>)new[] { 8863 }) },
                Array.Empty<int>()));

            // 0a03 109f45 — the objective, three bytes, with no flag behind it.
            Assert.Contains("0a03109f45", done);
            Assert.DoesNotContain("109f452001", done);
        }

        [Fact]
        public void An_empty_journal_is_still_a_message()
        {
            // What a new character gets, and it has to go out: the client fills the window from
            // this message, so sending nothing at all leaves whatever was there before.
            byte[] empty = QuestProtocol.BuildJournal(
                Array.Empty<(int, int, IReadOnlyList<int>, IReadOnlyCollection<int>)>(),
                Array.Empty<int>());

            // Only the empty f4 survives, which is what the capture also puts last.
            Assert.Equal("2200", Hex(empty));
        }
    }
}
