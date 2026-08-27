using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    public class CombatTurnCarouselTests
    {
        [Fact]
        public void Carousel_uses_engine_turn_order_and_appends_passive_summons()
        {
            var fight = new FightInstance(1, 1);
            var player = FighterWith(10, 400);
            var secondPlayer = FighterWith(11, 100);
            var monster = FighterWith(-1, 500, true);
            var secondMonster = FighterWith(-2, 300, true);

            fight.AddPlayer(player);
            fight.AddPlayer(secondPlayer);
            fight.AddMonster(monster);
            fight.AddMonster(secondMonster);

            var passive = FighterWith(-3, 0, true);
            passive.JuegaTurno = false;
            fight.Invocar(passive, player);

            Assert.Equal(new long[] { -1, 10, -2, 11 },
                         fight.TurnOrder.Select(fighter => fighter.Id));
            Assert.Equal(new long[] { -1, 10, -2, 11, -3 },
                         FightHandler.CombatantsInTurnOrder(fight));
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
