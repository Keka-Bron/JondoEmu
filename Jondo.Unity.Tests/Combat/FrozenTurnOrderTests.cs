using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The order a fight starts with is the one played, whatever the initiatives do after. It was
    /// rebuilt by the initiatives of the moment on every summon or death: in a dream, where the
    /// dreamer's sheet is brought to his level, the first monster slid from place 0 to 3 keeping
    /// its turn and the three before it -- the Osamodas among them -- lost theirs in round one.
    /// </summary>
    public class FrozenTurnOrderTests
    {
        private static FightInstance Fight(out Fighter player, out Fighter first)
        {
            var fight = new FightInstance(1, 1);
            player = new Fighter { Id = 100, MaxHP = 500, CurrentHP = 500, Initiative = 500 };
            fight.AddPlayer(player);
            first = new Fighter { Id = -1, IsMonster = true, MaxHP = 100, CurrentHP = 100, Initiative = 900 };
            fight.AddMonster(first);
            fight.AddMonster(new Fighter { Id = -2, IsMonster = true, MaxHP = 100, CurrentHP = 100, Initiative = 300 });
            fight.StartFight();
            return fight;
        }

        [Fact]
        public void An_initiative_raised_after_the_start_moves_nobody()
        {
            var fight = Fight(out var player, out var first);
            Assert.Same(first, fight.CurrentFighter);
            var before = fight.TurnOrder.ConvertAll(f => f.Id);

            // The player's initiative grows past the first monster's, then a summon comes in.
            player.Initiative = 2000;
            fight.Invocar(new Fighter { Id = -9, MaxHP = 50, CurrentHP = 50, JuegaTurno = true }, first);

            Assert.Same(first, fight.CurrentFighter);
            Assert.Equal(0, fight.CurrentTurnIndex);
            Assert.Equal(before[0], fight.TurnOrder[0].Id);
            Assert.True(fight.TurnOrder.IndexOf(player) > 0, "the player kept his place behind the first monster");
        }

        [Fact]
        public void Before_the_start_the_initiatives_still_decide()
        {
            var fight = new FightInstance(1, 1);
            var player = new Fighter { Id = 100, MaxHP = 500, CurrentHP = 500, Initiative = 500 };
            fight.AddPlayer(player);
            fight.AddMonster(new Fighter { Id = -1, IsMonster = true, MaxHP = 100, CurrentHP = 100, Initiative = 900 });
            player.Initiative = 2000;
            fight.UpdateTurnOrder();
            Assert.Same(player, fight.TurnOrder[0]);
        }
    }
}
