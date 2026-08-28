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

            Assert.True(FightHandler.DeferFightEndUntilAck(fight, 42));
            Assert.Equal(42, fight.FinPendiente);
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
