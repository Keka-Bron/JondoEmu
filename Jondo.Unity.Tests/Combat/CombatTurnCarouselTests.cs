using Jondo.Unity.Server.Handlers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    public class CombatTurnCarouselTests
    {
        [Fact]
        public void Death_ahead_of_the_current_fighter_keeps_jzc_index_inside_the_refreshed_list()
        {
            var fight = FightWithFasterAndSlowerMonsters();
            fight.StartFight();
            fight.NextTurn();

            Assert.Equal(10, fight.CurrentFighter.Id);
            Assert.Equal(1, fight.CurrentTurnIndex);

            fight.Team1.Single(fighter => fighter.Id == -1).CurrentHP = 0;

            Assert.Equal(new long[] { 10, -2 }, FightHandler.CombatantsInTurnOrder(fight));
            Assert.Equal(0, FightHandler.CarouselTurnIndex(fight));

            fight.NextTurn();

            Assert.Equal(-2, fight.CurrentFighter.Id);
            Assert.Equal(1, FightHandler.CarouselTurnIndex(fight));
        }

        [Fact]
        public void Passive_summons_are_appended_without_shifting_turn_indexes()
        {
            var fight = FightWithFasterAndSlowerMonsters();
            var player = fight.Team0.Single();
            var passive = FighterWith(-3, initiative: 0, monster: true);
            passive.JuegaTurno = false;
            fight.Invocar(passive, player);
            fight.StartFight();
            fight.NextTurn();

            Assert.Equal(new long[] { -1, 10, -2, -3 },
                         FightHandler.CombatantsInTurnOrder(fight));
            Assert.Equal(1, FightHandler.CarouselTurnIndex(fight));
        }

        private static FightInstance FightWithFasterAndSlowerMonsters()
        {
            var fight = new FightInstance(1, 1);
            fight.AddPlayer(FighterWith(10, initiative: 400));
            fight.AddMonster(FighterWith(-1, initiative: 500, monster: true));
            fight.AddMonster(FighterWith(-2, initiative: 300, monster: true));
            return fight;
        }

        private static Fighter FighterWith(long id, int initiative, bool monster = false)
            => new Fighter
            {
                Id = id,
                Initiative = initiative,
                IsMonster = monster,
                MaxHP = 100,
                CurrentHP = 100,
            };
    }
}
