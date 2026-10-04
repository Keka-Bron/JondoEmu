using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// What a character keeps of the turn he passes: half of what was left, carried into the next
    /// jzc's f4, and never beyond a minute and a half of turn.
    /// </summary>
    public class TurnTimeTests
    {
        /// <summary>"bastante pelea con hipermago": turns of 360, passed with 83, 243 and 344 tenths left.</summary>
        [Theory]
        [InlineData(83, 360, 41)]
        [InlineData(243, 360, 121)]
        [InlineData(344, 360, 172)]
        [InlineData(0, 360, 0)]          // passed by the clock: nothing left, nothing kept
        [InlineData(1000, 370, 500)]
        [InlineData(1200, 370, 530)]     // 370 + 530 = 900, a minute and a half
        public void Half_of_what_is_left_is_kept_up_to_a_minute_and_a_half(int left, int turn, int kept)
        {
            Assert.Equal(kept, FightProtocol.SavedAfter(left, turn));
        }

        /// <summary>The jzc of that capture's second turn: "{1: the player, 2: 360, 4: 41, 8: 2}".</summary>
        [Fact]
        public void The_kept_time_travels_in_the_jzcs_f4()
        {
            var jzc = ProtoMessage.Parse(FightProtocol.BuildTurnStart(879988113698, 360, 0, 2, carried: 41));
            long Field(int n) => jzc.Fields.Single(f => f.FieldNumber == n).VarIntValue;
            Assert.Equal((879988113698L, 360L, 41L, 2L), (Field(1), Field(2), Field(4), Field(8)));

            var jyt = ProtoMessage.Parse(FightProtocol.BuildTurnEnd(879988113698, 41));
            Assert.Equal(41, jyt.Fields.Single(f => f.FieldNumber == 1).VarIntValue);
        }

        [Fact]
        public void Only_a_character_keeps_his_time()
        {
            Assert.True(FightHandler.KeepsTurnTime(new Fighter { Id = 1 }));
            Assert.False(FightHandler.KeepsTurnTime(new Fighter { Id = -1, IsMonster = true }));
            Assert.False(FightHandler.KeepsTurnTime(new Fighter { Id = -2, Invocador = 1 }));
            Assert.False(FightHandler.KeepsTurnTime(new Fighter { Id = 900_000_000_000_001, IsBot = true }));
        }

        /// <summary>Coming back in the middle of a turn: what is left counts the carried time.</summary>
        [Fact]
        public void What_is_left_of_a_turn_counts_the_carried_time()
        {
            var now = System.DateTime.UtcNow;
            var turn = new FightInstance.AnnouncedTurn(1, 0, 2, 360, now.AddSeconds(-10), Carried: 41);
            Assert.InRange(turn.RemainingDeciseconds(now), 300, 301);
        }
    }
}
