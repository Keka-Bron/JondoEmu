using System;
using System.Linq;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Coming back into a fight: the frames of the reconnection captures, byte for byte, and the
    /// arithmetic of the turn in progress.
    /// </summary>
    public class FightReconnectionTests
    {
        /// <summary>The jzc of the resume burst at 30.90 s: 350 turn, 132 tenths left, round 1, no f7.</summary>
        [Fact]
        public void The_turn_in_progress_says_what_is_left_of_it()
        {
            byte[] jzc = FightProtocol.BuildTurnResumed(302677754146, 350, remaining: 132, round: 1);

            Assert.Equal("08a28280c8e70810de023084014001", Hex(jzc));
        }

        /// <summary>And the ordinary jzc is untouched by it: the one at 9.09 s.</summary>
        [Fact]
        public void An_ordinary_turn_start_carries_no_f6()
        {
            byte[] jzc = FightProtocol.BuildTurnStart(302677754146, 350, index: 0, round: 1);

            Assert.Equal("08a28280c8e70810de024001", Hex(jzc));
        }

        /// <summary>
        /// The kaa of a fight in progress: f1 = 1 and no countdown, "0801180120013004" in both
        /// resumes of the capture. The placement one, "1801200128bc033004", is what sent the
        /// returning client back to the READY button.
        /// </summary>
        [Fact]
        public void The_resumes_kaa_says_the_fight_is_on()
        {
            Assert.Equal("0801180120013004", Hex(FightProtocol.BuildFightInProgressSummary(4)));
            Assert.Equal("1801200128bc033004", Hex(FightProtocol.BuildFightSummary(4, 444)));
        }

        /// <summary>The lqn behind the lqu of the fight-flavoured map block.</summary>
        [Fact]
        public void Back_in_the_fight_is_text_184_of_type_1()
        {
            byte[] lqn = ConnectionProtocol.BuildBackInTheFight("Sacri-Master");

            Assert.Equal("080110b801220c53616372692d4d6173746572", Hex(lqn));
        }

        /// <summary>
        /// The capture: the turn of 350 started 21.8 seconds before the burst and the burst
        /// says 132. Ours counts the same way, and never below zero.
        /// </summary>
        [Fact]
        public void What_is_left_of_a_turn_is_counted_in_tenths()
        {
            var started = new DateTime(2026, 8, 9, 21, 0, 0, DateTimeKind.Utc);
            var turn = new FightInstance.AnnouncedTurn(302677754146, 0, 1, 350, started);

            Assert.Equal(132, turn.RemainingDeciseconds(started.AddMilliseconds(21800)));
            Assert.Equal(350, turn.RemainingDeciseconds(started));
            Assert.Equal(0, turn.RemainingDeciseconds(started.AddSeconds(60)));
        }

        [Fact]
        public void A_turn_never_announced_is_not_one()
        {
            var none = default(FightInstance.AnnouncedTurn);

            Assert.False(none.Announced);
            Assert.Equal(0, none.RemainingDeciseconds(DateTime.UtcNow));
        }

        /// <summary>A jwq of a running fight is the jxm bodies, one f1 each; an empty one is empty.</summary>
        [Fact]
        public void The_buff_sync_is_the_jxm_bodies_in_a_row()
        {
            byte[] one = FightProtocol.BuildBuff(-3, 302677754146, 1, 950, 299043, 3793, 0, 0, 25188, "I", -1, 0, 2);

            Assert.Empty(FightProtocol.BuildBuffSync(Array.Empty<byte[]>()));
            byte[] jwq = FightProtocol.BuildBuffSync(new[] { one, one });
            Assert.Equal("0a" + one.Length.ToString("x2") + Hex(one) + "0a" + one.Length.ToString("x2") + Hex(one),
                         Hex(jwq));
        }

        /// <summary>Nothing has been confirmed on a fresh fight, so its first turn is waiting.</summary>
        [Fact]
        public void A_fresh_fight_is_waiting_for_its_first_confirmation()
        {
            var fight = new FightInstance(1, 100, 100);

            Assert.True(fight.TurnAwaitingConfirmation);
            Assert.True(fight.AtenderElTurnoUnaVez(fight.RoundNumber, fight.CurrentTurnIndex));
            Assert.False(fight.TurnAwaitingConfirmation);
        }

        private static string Hex(byte[]? bytes)
            => bytes == null ? "<null>" : string.Concat(bytes.Select(b => b.ToString("x2")));
    }
}
