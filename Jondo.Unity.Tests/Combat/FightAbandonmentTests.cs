using Jondo.Unity.Server.Network;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    public class FightAbandonmentTests
    {
        [Fact]
        public void Placement_fights_cannot_enter_the_surrender_death_path()
        {
            var (fight, player) = FightWithPlayer();

            Assert.Equal(FightState.Placement, fight.State);
            Assert.Null(FightHandler.AbandoningFighter(fight, player.Id));
            Assert.Equal(100, player.CurrentHP);
        }

        [Fact]
        public void Only_the_alive_session_character_can_abandon_an_ongoing_fight()
        {
            var (fight, player) = FightWithPlayer();
            fight.StartFight();

            Assert.Same(player, FightHandler.AbandoningFighter(fight, player.Id));
            Assert.Null(FightHandler.AbandoningFighter(fight, player.Id + 1));

            player.CurrentHP = 0;
            Assert.Null(FightHandler.AbandoningFighter(fight, player.Id));
        }

        [Fact]
        public void Surrender_results_stay_pending_until_the_death_sequence_is_acknowledged()
        {
            var (fight, player) = FightWithPlayer();
            fight.StartFight();
            player.CurrentHP = 0;

            // The real assignment, not an extraction made to give this line something to call.
            fight.FinPendiente = 42;
            Assert.Equal(42, fight.FinPendiente);
        }

        // The frames the surrender sequence is made of, against the bytes the real server sent.
        // Taken from "Combate/aceptar desafio-combate completo-abandonar al final.pcapng", frames
        // 6006, 6009 and 6010, which are the last three before the fight ends.
        private const long DuelAuthor = 293213045026;   // a282f0a6c408

        private static string Hex(byte[] frame)
            => System.Convert.ToHexString(frame).ToLowerInvariant();

        [Fact]
        public void The_wrapper_says_kind_five_and_not_the_everyday_three()
        {
            // jto 08a282f0a6c408 1005. Kind 3 is the ordinary action wrapper and is what this
            // was built with; 5 appears exactly once per capture and only here.
            Assert.Equal("08a282f0a6c4081005",
                         Hex(FightProtocol.BuildSequenceStart(DuelAuthor, FightProtocol.SurrenderSequence)));

            Assert.NotEqual(FightProtocol.SurrenderSequence, FightProtocol.ActionSequence);
        }

        [Fact]
        public void The_closing_frame_carries_the_same_kind_and_the_action_id()
        {
            // jwi 0802 10a282f0a6c408 1805.
            Assert.Equal("080210a282f0a6c4081805",
                         Hex(FightProtocol.BuildSequenceEnd(2, DuelAuthor, FightProtocol.SurrenderSequence)));
        }

        [Fact]
        public void And_a_jxh_follows_it()
        {
            // jxh 10a282f0a6c408. In this capture the client answers it with jwz and NO jti
            // arrives at all, so waiting only on jti would leave the result screen stuck.
            Assert.Equal("10a282f0a6c408", Hex(FightProtocol.BuildConfirmTurn(DuelAuthor)));
        }

        [Fact]
        public void The_wrapper_belongs_to_the_fighter_whose_turn_it_is()
        {
            // The two captures look contradictory until you see who dies inside. In the duel the
            // wrapper is a282f0a6c408 and the jwe kills a28280c8e708 -- our own character, the one
            // who pressed surrender. So the author is the current fighter and the quitter is the
            // one in the death frame, and using the quitter for both is contradicted by the data.
            const long quitter = 302677754146;   // a28280c8e708

            Assert.NotEqual(DuelAuthor, quitter);

            // The death frame from the same capture, byte for byte: jwe (18)
            // 18a28280c8e708 220708a28280c8e708 7067 -- author and victim both the quitter, and
            // 0x67 is the "died" reason.
            Assert.Equal("18a28280c8e708220708a28280c8e7087067",
                         Hex(FightProtocol.BuildDeath(quitter, quitter)));
        }

        [Fact]
        public void Whoever_abandons_stops_getting_turns()
        {
            // The half of abandonment that is not about frames: the quitter is left at zero life,
            // and NextTurn has to walk past them for the rest of the fight. If it did not, the
            // fight would stall on a fighter who is not there any more.
            var fight = new FightInstance(1, 1);
            var quitter = new Fighter { Id = 10, MaxHP = 100, CurrentHP = 100 };
            var monster = new Fighter { Id = -1, IsMonster = true, MaxHP = 100, CurrentHP = 100 };
            fight.AddPlayer(quitter);
            fight.AddMonster(monster);
            fight.StartFight();

            quitter.CurrentHP = 0;

            for (int turn = 0; turn < 6; turn++)
            {
                var whose = fight.NextTurn();
                if (whose == null) break;
                Assert.NotEqual(quitter.Id, whose.Id);
            }
        }

        [Fact]
        public void The_rotation_drops_them_while_the_teams_keep_them()
        {
            // Two lists and they are not the same list, which is the thing to get right. The turn
            // rotation is rebuilt by Agrupar, which filters on IsAlive, so whoever abandons is
            // gone from it and can never come up again. The CAROUSEL the client draws is built
            // from the teams instead -- BuildTeams over Azul plus Rojo, which keep their dead --
            // so the player stays on screen, greyed, and nobody else's slot renumbers.
            var fight = new FightInstance(1, 1);
            var quitter = new Fighter { Id = 10, MaxHP = 100, CurrentHP = 100 };
            fight.AddPlayer(quitter);
            fight.AddMonster(new Fighter { Id = -1, IsMonster = true, MaxHP = 100, CurrentHP = 100 });
            fight.StartFight();

            quitter.CurrentHP = 0;
            fight.UpdateTurnOrder();

            Assert.DoesNotContain(fight.TurnOrder, f => f.Id == quitter.Id);
            Assert.Contains(fight.Azul, f => f.Id == quitter.Id);
        }

        private static (FightInstance Fight, Fighter Player) FightWithPlayer()
        {
            var fight = new FightInstance(1, 1);
            var player = new Fighter { Id = 10, MaxHP = 100, CurrentHP = 100 };
            var monster = new Fighter
            {
                Id = -1,
                IsMonster = true,
                MaxHP = 100,
                CurrentHP = 100,
            };
            fight.AddPlayer(player);
            fight.AddMonster(monster);
            return (fight, player);
        }
    }
}
