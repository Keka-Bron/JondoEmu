using Jondo.Unity.World.Combat;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Which effects the fight engine can actually apply.
    /// </summary>
    /// <remarks>
    /// This is the classification the spell screen shows and the engine obeys, and they share it so
    /// they cannot disagree: <c>EffectEngine</c> takes its own constants from
    /// <see cref="EffectSupport"/>, so an effect gaining an implementation without being added here
    /// would not compile.
    ///
    /// Measured over the 872 effects in <c>world.db</c>: 23 have code, 205 are applied as a
    /// characteristic, and <b>644 do nothing at all</b> — and thousands of spell levels
    /// carry at least one of those 644.
    /// </remarks>
    public class EffectSupportTests
    {
        /// <summary>
        /// Effect 108 is healing, and it does nothing. The card says it heals, the animation plays,
        /// and nobody's life goes up. It is on 751 spell levels.
        /// </summary>
        [Fact]
        public void Healing_is_still_not_implemented_and_says_so()
        {
            // Its catalogue row carries no characteristic, which is exactly why it falls through.
            Assert.Equal(EffectSupportKind.PanelOnly, EffectSupport.Classify(108, 0, 2));
            Assert.DoesNotContain(108, EffectSupport.HandledDirectly);
        }

        [Theory]
        [InlineData(EffectSupport.Push)]
        [InlineData(EffectSupport.Pull)]
        [InlineData(EffectSupport.StepBack)]
        [InlineData(EffectSupport.StepForward)]
        [InlineData(EffectSupport.AddState)]
        [InlineData(EffectSupport.RemoveState)]
        [InlineData(EffectSupport.CastSpell)]
        [InlineData(EffectSupport.TriggerSpell)]
        [InlineData(EffectSupport.RemoveSpellEffects)]
        [InlineData(EffectSupport.ChangeLook)]
        [InlineData(EffectSupport.Summon)]
        [InlineData(EffectSupport.HealPercent)]
        [InlineData(EffectSupport.Kill)]
        public void The_effects_with_code_are_reported_as_such(int effectId)
        {
            Assert.Contains(effectId, EffectSupport.HandledDirectly);

            // Direct beats everything: an effect with code is applied whatever its catalogue row
            // says, which is why the order inside Classify matters.
            Assert.Equal(EffectSupportKind.Direct, EffectSupport.Classify(effectId, 0, 0));
            Assert.Equal(EffectSupportKind.Direct, EffectSupport.Classify(effectId, 12, 2));
        }

        /// <summary>The damage effects are a range, 91 to 100, and all ten are handled.</summary>
        [Fact]
        public void Every_damage_effect_in_the_range_is_handled()
        {
            for (int effect = EffectSupport.FirstDamage; effect <= EffectSupport.LastDamage; effect++)
            {
                Assert.Equal(EffectSupportKind.Direct, EffectSupport.Classify(effect, 0, 0));
            }

            Assert.DoesNotContain(EffectSupport.FirstDamage - 1, EffectSupport.HandledDirectly);
            Assert.DoesNotContain(EffectSupport.LastDamage + 1, EffectSupport.HandledDirectly);
        }

        /// <summary>
        /// An effect with a characteristic is applied generically, without the engine knowing what
        /// it is. 205 of the game's 872 work this way.
        /// </summary>
        [Fact]
        public void An_effect_with_a_characteristic_is_applied()
            => Assert.Equal(EffectSupportKind.Characteristic, EffectSupport.Classify(2000, 12, 0));

        /// <summary>
        /// Category 2 is the weapon-only effects, and the engine's catalogue query skips them. An
        /// effect that only exists on weapons is not applied when a spell carries it.
        /// </summary>
        [Fact]
        public void A_weapon_only_effect_is_not_applied_from_a_spell()
            => Assert.Equal(EffectSupportKind.PanelOnly,
                            EffectSupport.Classify(2000, 12, EffectSupport.WeaponCategory));

        [Fact]
        public void An_effect_with_nothing_at_all_does_nothing()
            => Assert.Equal(EffectSupportKind.PanelOnly, EffectSupport.Classify(9999, 0, 0));

        /// <summary>
        /// A characteristic of zero is not a characteristic. This is the exact condition that
        /// leaves healing dead, so it is worth its own check.
        /// </summary>
        [Fact]
        public void A_characteristic_of_zero_does_not_count()
            => Assert.Equal(EffectSupportKind.PanelOnly, EffectSupport.Classify(2000, 0, 0));
    }
}
