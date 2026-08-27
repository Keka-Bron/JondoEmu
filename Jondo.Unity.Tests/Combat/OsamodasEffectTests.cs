using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Combat;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    public class OsamodasEffectTests
    {
        [Fact]
        public void Gobgob_uses_the_same_ground_summon_path_as_regular_summons()
        {
            Assert.True(EffectEngine.VaAlSuelo(EffectSupport.Summon));
            Assert.True(EffectEngine.VaAlSuelo(EffectSupport.ControllableSummon));
        }

        [Theory]
        [InlineData(EffectSupport.CasterCurrentHealthDamage)]
        [InlineData(EffectSupport.CasterMissingHealthDamage)]
        [InlineData(EffectSupport.BestElementDamage)]
        public void Osamodas_special_damage_is_part_of_the_damage_pipeline(int effectId)
            => Assert.True(EffectEngine.EsDeDano(effectId));

        [Theory]
        [InlineData(EffectSupport.Heal)]
        [InlineData(EffectSupport.WaterHeal)]
        [InlineData(EffectSupport.AirHeal)]
        [InlineData(EffectSupport.EarthHeal)]
        public void Every_elemental_Osamodas_heal_is_resolved_as_healing(int effectId)
            => Assert.True(EffectEngine.EsCuraFija(effectId));
    }
}
