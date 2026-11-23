using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The burn specials of the Mirador de Ilyzaelle's spirits: Combustión Espontánea (9064) and
    /// its kin only touch an enemy who burns (1160 and 951 on «A,E556»). Weighed as a mechanic
    /// on any enemy, they were cast at one who did not burn and did nothing.
    /// </summary>
    public class BurnSpecialTests
    {
        private const int CombustionEspontanea = 9064;
        private const int Burning = 556;

        [Fact]
        public void A_burn_special_is_worth_something_only_on_an_enemy_who_burns()
        {
            var spirit = new Fighter
            {
                Id = -1, TeamId = 1, MonsterId = 3000, IsMonster = true, Level = 200,
                MaxAP = 10, CurrentAP = 10, MaxHP = 4000, CurrentHP = 4000,
                HechizosDeInvocado = new[] { (CombustionEspontanea, 1) },
            };
            var spell = FightHandler.TacticsOf(spirit).SingleOrDefault(s => s.Id == CombustionEspontanea);
            if (spell == null) return;   // no spell data

            var player = new Fighter
            {
                Id = 10, TeamId = 0, MaxHP = 5000, CurrentHP = 5000, MaxAP = 12, CurrentAP = 12, Level = 200,
            };
            Assert.True(spell.UtilityOnEnemies);
            Assert.False(spell.UtilityReachesEnemy(spirit, player));

            player.Buffs.PonerEstado(Burning);
            Assert.True(spell.UtilityReachesEnemy(spirit, player));
        }

        [Fact]
        public void Only_masks_that_all_ask_for_a_state_narrow_the_enemies()
        {
            Assert.Equal(new[] { "A,E556" }, FightHandler.EnemyMasksAskingAState(new[] { "A,E556", "A,E556", "C" }));
            Assert.Empty(FightHandler.EnemyMasksAskingAState(new[] { "A,E556", "A" }));
            Assert.Empty(FightHandler.EnemyMasksAskingAState(new[] { "a,E556" }));
        }
    }
}
