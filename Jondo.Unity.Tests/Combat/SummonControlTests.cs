using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Who plays a summon: the Arakne is its owner's («Invoque une Arakne maîtrisable»), the
    /// Chaferfu plays on its own. Both were the owner's, as every summon with a spell is.
    /// </summary>
    public class SummonControlTests
    {
        private static Fighter Summoned(int template)
        {
            var owner = new Fighter { Id = 10, Level = 200, MaxHP = 3000, CurrentHP = 3000 };
            var recipe = Summons.De(template, 3);
            if (recipe == null) return null;
            var summon = new Fighter { Id = -1, IsMonster = true, MonsterId = template, Invocador = owner.Id };
            Summons.Characteristics(summon, recipe, owner);
            return summon;
        }

        [Theory]
        [InlineData(262)]    // Chaferfu
        [InlineData(1108)]   // Chaferfu Lancier
        public void A_chaferfu_plays_on_its_own(int template)
        {
            var summon = Summoned(template);
            if (summon == null) return;   // no monster data

            Assert.True(summon.PlaysOnItsOwn);
            Assert.False(summon.ControlledBy(10));
        }

        [Theory]
        [InlineData(246)]    // Arakne
        [InlineData(2630)]   // Arakne Majeure
        public void An_arakne_is_its_owner_s(int template)
        {
            var summon = Summoned(template);
            if (summon == null) return;   // no monster data

            Assert.False(summon.PlaysOnItsOwn);
            Assert.True(summon.ControlledBy(10));
        }
    }
}
