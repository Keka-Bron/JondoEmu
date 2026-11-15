using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Sramurái's Sentido del Deber (4727, Ring of Captain Eskarlata): one row waiting on its
    /// caster being hurt (D), which then casts 4726, a movement point for everybody round him.
    /// Nothing in it acts at the cast, so the tactics weighed it as nothing and it was never cast.
    /// </summary>
    public class HookOnlySpellTests
    {
        private const int SentidoDelDeber = 4727;

        [Fact]
        public void A_spell_that_only_hooks_its_caster_is_a_mechanic_cast_on_himself()
        {
            var sramurai = new Fighter
            {
                Id = -1, TeamId = 1, MonsterId = 3732, IsMonster = true, Level = 130,
                MaxAP = 8, CurrentAP = 8, MaxHP = 2000, CurrentHP = 2000,
                HechizosDeInvocado = new[] { (SentidoDelDeber, 1) },
            };
            var spell = FightHandler.TacticsOf(sramurai).SingleOrDefault(s => s.Id == SentidoDelDeber);
            if (spell == null) return;   // no spell data

            Assert.False(spell.Offensive);
            Assert.True(spell.Utility > 0, "Sentido del Deber weighed as nothing");
            Assert.False(spell.UtilityOnEnemies);
            Assert.Equal(3, spell.Interval);
        }

        /// <summary>
        /// Its row has no duration; armed on himself as the last turn of the round, it lasts to his
        /// next turn, through the enemy's in between. To the next round alone, nobody ever hit it.
        /// </summary>
        [Fact]
        public void Its_hook_lives_to_the_caster_s_next_turn()
        {
            if (SpellEffects.De(SentidoDelDeber, 1).Count == 0) return;   // no spell data

            var fight = new FightInstance(3, 1);
            var player = new Fighter
            {
                Id = 10, TeamId = 0, CellId = 300, MaxHP = 1000, CurrentHP = 1000, MaxAP = 6, CurrentAP = 6,
                MaxMP = 3, CurrentMP = 3, Level = 150, Initiative = 5000,
            };
            var sramurai = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 217, MaxHP = 2000, CurrentHP = 2000, MaxAP = 10, CurrentAP = 10,
                MaxMP = 4, CurrentMP = 4, Level = 130, IsMonster = true, MonsterId = 3732, Initiative = 10,
            };
            fight.AddPlayer(player);
            fight.AddMonster(sramurai);
            fight.UpdateTurnOrder();
            while (fight.CurrentFighter != sramurai) fight.NextTurn();
            Assert.Equal(sramurai, fight.TurnOrder.Last());

            var armed = EffectEngine.Resolver(fight, sramurai, SentidoDelDeber, 1, sramurai, EffectEngine.AlLanzar,
                                                       fight.RoundNumber, celdaApuntada: sramurai.CellId)
                .Where(o => o.FilaArmada && o.Sobre == sramurai).ToList();

            Assert.NotEmpty(armed);
            Assert.All(armed, o => Assert.Equal(fight.RoundNumber + 2, o.HookUntil));
        }
    }
}
