using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A summon is born with its spells' InitialCooldown, as a monster placed at the start is. The
    /// Hamrack that Ben el Ripata's turtle summons carries Bandazo, which waits four turns and then
    /// blows it up; it was cast on the Hamrack's first turn, so it died before the fight's
    /// mechanic could use it.
    /// </summary>
    public class SummonBornWaitingTests
    {
        private const int Hamrack = 2955, Bandazo = 682;

        [Fact]
        public void A_summoned_monster_waits_on_its_spells_initial_cooldown()
        {
            var recipe = Summons.De(Hamrack, 1);
            if (recipe == null || !recipe.Hechizos.Any(h => h.Spell == Bandazo)) return;   // no monster data

            var hamrack = new Fighter { Id = -5, MonsterId = Hamrack, IsMonster = true };
            Summons.Characteristics(hamrack, recipe, new Fighter { Id = -1, Level = 140, IsMonster = true });
            FightHandler.SpellsBornWaiting(hamrack);

            Assert.True(hamrack.Recarga.TryGetValue(Bandazo, out int wait));
            Assert.Equal(4, wait);
        }
    }
}
