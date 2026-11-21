using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.World.Content;
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
            var jzc = ProtoMessage.Parse(FightProtocol.BuildTurnStart(870000000004, 360, 0, 2, carried: 41));
            long Field(int n) => jzc.Fields.Single(f => f.FieldNumber == n).VarIntValue;
            Assert.Equal((870000000004L, 360L, 41L, 2L), (Field(1), Field(2), Field(4), Field(8)));

            var jyt = ProtoMessage.Parse(FightProtocol.BuildTurnEnd(870000000004, 41));
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

        /// <summary>
        /// A server not started for the test harness -- the one players download -- gives the real
        /// game's clocks: a character 40 s (the captures run from 30 to 43), a monster 29, a summon
        /// 15 and a character in a dream 90.
        /// </summary>
        [Fact]
        public void A_server_not_started_for_the_harness_gives_the_real_games_turns()
        {
            var fight = new FightInstance(1, 1);
            var character = new Fighter { Id = 1 };
            Assert.Equal(400, FightHandler.TurnDeciseconds(fight, character, harness: false));
            Assert.Equal(290, FightHandler.TurnDeciseconds(fight, new Fighter { Id = -1, IsMonster = true }, harness: false));
            Assert.Equal(150, FightHandler.TurnDeciseconds(fight, new Fighter { Id = -2, Invocador = 1 }, harness: false));
            fight.Reglas = FightRules.EnUnSueno;
            Assert.Equal(900, FightHandler.TurnDeciseconds(fight, character, harness: false));
        }

        /// <summary>The harness's hour is a character's alone: the monsters keep their 29 seconds.</summary>
        [Fact]
        public void Only_the_harness_waits_an_hour_and_only_for_a_character()
        {
            var fight = new FightInstance(1, 1);
            Assert.Equal(36000, FightHandler.TurnDeciseconds(fight, new Fighter { Id = 1 }, harness: true));
            Assert.Equal(290, FightHandler.TurnDeciseconds(fight, new Fighter { Id = -1, IsMonster = true }, harness: true));
            // A summon nobody in the fight plays.
            Assert.Equal(150, FightHandler.TurnDeciseconds(fight, new Fighter { Id = -2, Invocador = 1 }, harness: true));
        }

        /// <summary>
        /// The guided tutorial's Puch celestial: every jzc of its fight in the capture of the whole
        /// tutorial carries 6000 for the character and 290 for the puch, and no f4: nothing is kept
        /// of a ten-minute turn, which already passes the minute and a half.
        /// </summary>
        [Fact]
        public void A_group_can_give_its_fight_ten_minute_turns_and_none_is_kept()
        {
            var fight = new FightInstance(1, 1) { PlayerTurn = 6000 };
            Assert.Equal(6000, FightHandler.TurnDeciseconds(fight, new Fighter { Id = 1 }, harness: false));
            Assert.Equal(290, FightHandler.TurnDeciseconds(fight, new Fighter { Id = -1, IsMonster = true }, harness: false));
            Assert.Equal(0, FightProtocol.SavedAfter(5000, 6000));
        }

        /// <summary>Only the Puch celestial's group asks for it; the pen's Jalatín fights at the usual clock.</summary>
        [Fact]
        public void The_written_puch_of_the_tutorial_carries_its_ten_minutes()
        {
            var groups = MobGroupContent.Load(Paths.ContentFile(MobGroupContent.AuthoredFile), _ => { }).Values.ToList();
            var puch = groups.Single(g => g.MapId == 241438721 && g.Members.Any(m => m.MonsterId == 2785));
            Assert.Equal(6000, puch.PlayerTurn);
            var pen = groups.Single(g => g.MapId == 241439745 && g.Members.Any(m => m.MonsterId == 8017));
            Assert.Equal(0, pen.PlayerTurn);
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
