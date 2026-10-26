using Jondo.Unity.Server;
using Xunit;

namespace Jondo.Unity.Tests.Economy
{
    /// <summary>
    /// A brand-new item's effect, however it is born -- a command, a reward, a purchase from an
    /// NPC --: a weapon's damage keeps its range, anything else takes the top of its die. Rolled
    /// apart in the NPC shops, the Hacha Jazo came out as "[96,60,0,0]" and its tooltip said
    /// «0 de daños de agua».
    /// </summary>
    public class FreshEffectTests
    {
        [Fact]
        public void A_weapon_s_damage_keeps_its_range()
            => Assert.Equal("[96,0,46,60]", DatabaseManager.FreshEffect(96, 46, 60, 0, DatabaseManager.WeaponDamageCategory));

        [Fact]
        public void A_characteristic_takes_the_top_of_its_die()
            => Assert.Equal("[125,350,0,0]", DatabaseManager.FreshEffect(125, 301, 350, 0, 0));

        [Fact]
        public void A_fixed_value_stays_fixed()
            => Assert.Equal("[160,8,0,0]", DatabaseManager.FreshEffect(160, 0, 0, 8, 0));
    }
}
