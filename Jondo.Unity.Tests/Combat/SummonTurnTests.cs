using System.Linq;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    public class SummonTurnTests
    {
        [Fact]
        public void Active_summons_play_after_their_owner_while_passive_ones_stay_off_the_clock()
        {
            var fight = new FightInstance(1, 1);
            var owner = new Fighter
            {
                Id = 10,
                Initiative = 100,
                MaxHP = 100,
                CurrentHP = 100,
            };
            var enemy = new Fighter
            {
                Id = -1,
                IsMonster = true,
                Initiative = 50,
                MaxHP = 100,
                CurrentHP = 100,
            };

            fight.AddPlayer(owner);
            fight.AddMonster(enemy);

            var active = new Fighter
            {
                Id = -2,
                IsMonster = true,
                JuegaTurno = true,
                MaxHP = 100,
                CurrentHP = 100,
                SpellIds = { 31166, 31168 },
            };
            var passive = new Fighter
            {
                Id = -3,
                IsMonster = true,
                JuegaTurno = false,
                MaxHP = 100,
                CurrentHP = 100,
            };

            fight.Invocar(active, owner);
            fight.Invocar(passive, owner);

            Assert.Equal(new long[] { owner.Id, active.Id, enemy.Id },
                         fight.TurnOrder.Select(f => f.Id));
            Assert.DoesNotContain(passive, fight.TurnOrder);
        }
    }
}
