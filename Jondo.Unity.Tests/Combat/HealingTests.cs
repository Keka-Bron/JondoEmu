using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Combat;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    public class HealingTests
    {
        [Fact]
        public void One_fixed_heal_roll_is_shared_across_the_zone_before_distance_falloff()
        {
            const int center = 200;
            int edge = MapGeometry.GetNeighbors(center).First();
            var fight = new FightInstance(1, 1);
            var caster = Fighter(1, 150, 1000, 1000);
            caster.Intelligence = 100;
            caster.Otras[49] = 5;
            var centerTarget = Fighter(2, center, 100, 1000);
            var edgeTarget = Fighter(3, edge, 100, 1000);
            fight.AddPlayer(caster);
            fight.AddPlayer(centerTarget);
            fight.AddPlayer(edgeTarget);

            var effect = FixedHeal(diceMin: 10, diceMax: 90, targetMask: "a");
            effect = new SpellEffect
            {
                EffectId = effect.EffectId,
                EffectUid = effect.EffectUid,
                DiceNum = effect.DiceNum,
                DiceSide = effect.DiceSide,
                Triggers = effect.Triggers,
                TargetMask = effect.TargetMask,
                Element = effect.Element,
                Forma = 'C',
                Tamano = 2,
                PasoDeCaida = 10,
                TopeDeCaida = 4,
            };
            int rolls = 0;

            var outcomes = EffectEngine.ResolveEffects(
                fight, caster, 100, 1, centerTarget, EffectEngine.AlLanzar, 1,
                new[] { effect }, aimedCell: center,
                rollEffect: _ =>
                {
                    rolls++;
                    return 40;
                });

            Assert.Equal(1, rolls);
            Assert.Equal(85, outcomes.Single(o => o.Sobre == centerTarget).Cura);
            Assert.Equal(77, outcomes.Single(o => o.Sobre == edgeTarget).Cura);
            Assert.Equal(185, centerTarget.CurrentHP);
            Assert.Equal(177, edgeTarget.CurrentHP);
        }

        [Fact]
        public void Immediate_healing_is_capped_by_missing_life()
        {
            var fight = new FightInstance(1, 1);
            var caster = Fighter(1, 100, 1000, 1000);
            var target = Fighter(2, 101, 975, 1000);
            fight.AddPlayer(caster);
            fight.AddPlayer(target);

            var outcomes = EffectEngine.ResolveEffects(
                fight, caster, 100, 1, target, EffectEngine.AlLanzar, 1,
                new[] { FixedHeal(100, 100) });

            Assert.Equal(25, Assert.Single(outcomes).Cura);
            Assert.Equal(1000, target.CurrentHP);
        }

        [Fact]
        public void Fixed_healing_reads_combat_bonuses_and_received_healing()
        {
            var fight = new FightInstance(1, 1);
            var caster = Fighter(1, 100, 1000, 1000);
            caster.Intelligence = 100;
            caster.Power = 10_000;
            caster.Otras[49] = 5;
            caster.Buffs.Poner(new Buff
            {
                EffectId = 1,
                EffectUid = 10,
                Caracteristica = 15,
                Cuanto = 50,
                Quien = caster.Id,
                EmpiezaEnRonda = 1,
                CaducaEnRonda = -1,
            }, fight.SiguienteEmbrujo);
            caster.Buffs.Poner(new Buff
            {
                EffectId = 2,
                EffectUid = 11,
                Caracteristica = 49,
                Cuanto = 2,
                Quien = caster.Id,
                EmpiezaEnRonda = 1,
                CaducaEnRonda = -1,
            }, fight.SiguienteEmbrujo);
            var target = Fighter(2, 101, 500, 1000);
            target.Buffs.Poner(new Buff
            {
                EffectId = 1159,
                EffectUid = 12,
                Cuanto = 50,
                Quien = target.Id,
                EmpiezaEnRonda = 1,
                CaducaEnRonda = -1,
            }, fight.SiguienteEmbrujo);
            fight.AddPlayer(caster);
            fight.AddPlayer(target);

            var outcomes = EffectEngine.ResolveEffects(
                fight, caster, 100, 1, target, EffectEngine.AlLanzar, 1,
                new[] { FixedHeal(40, 40) });

            Assert.Equal(54, Assert.Single(outcomes).Cura);
            Assert.Equal(554, target.CurrentHP);
        }

        [Fact]
        public void Percentage_healing_keeps_its_minimum_and_has_no_point_falloff()
        {
            const int center = 200;
            int edge = MapGeometry.GetNeighbors(center).First();
            var fight = new FightInstance(1, 1);
            var caster = Fighter(1, 150, 1000, 1000);
            var target = Fighter(2, edge, 500, 1000);
            fight.AddPlayer(caster);
            fight.AddPlayer(target);
            var effect = new SpellEffect
            {
                EffectId = EffectSupport.HealPercent,
                EffectUid = 2,
                DiceNum = 5,
                DiceSide = 10,
                Triggers = EffectEngine.AlLanzar,
                PasoDeCaida = 50,
                TopeDeCaida = 4,
            };

            var outcomes = EffectEngine.ResolveEffects(
                fight, caster, 100, 1, target, EffectEngine.AlLanzar, 1,
                new[] { effect }, aimedCell: center,
                rollEffect: _ => throw new InvalidOperationException(
                    "Percentage healing must preserve its existing non-random behavior."));

            Assert.Equal(50, Assert.Single(outcomes).Cura);
            Assert.Equal(550, target.CurrentHP);
        }

        [Theory]
        [InlineData(EffectSupport.FireHeal, 40, 40, 40)]
        [InlineData(EffectSupport.HealPercent, 10, 20, 100)]
        public void Delayed_healing_waits_then_activates_once(
            int effectId, int diceMin, int diceMax, int expected)
        {
            var fight = new FightInstance(1, 1);
            var caster = Fighter(1, 100, 1000, 1000);
            var target = Fighter(2, 101, 1000, 1000);
            fight.AddPlayer(caster);
            fight.AddPlayer(target);
            var effect = new SpellEffect
            {
                EffectId = effectId,
                EffectUid = 3,
                DiceNum = diceMin,
                DiceSide = diceMax,
                Delay = 2,
                Triggers = EffectEngine.AlLanzar,
            };

            var scheduled = EffectEngine.ResolveEffects(
                fight, caster, 100, 1, target, EffectEngine.AlLanzar, 1,
                new[] { effect });

            Assert.Equal(1000, target.CurrentHP);
            Assert.NotNull(Assert.Single(scheduled).Buff);
            target.CurrentHP = 900;
            Assert.Empty(EffectEngine.ActivateDelayedHealing(fight, 2));

            var activated = Assert.Single(EffectEngine.ActivateDelayedHealing(fight, 3));
            Assert.Equal(expected, activated.Healed);
            Assert.Equal(900 + expected, target.CurrentHP);
            Assert.Empty(EffectEngine.ActivateDelayedHealing(fight, 3));
        }

        [Fact]
        public void Healing_never_resurrects_a_dead_target()
        {
            var fight = new FightInstance(1, 1);
            var caster = Fighter(1, 100, 1000, 1000);
            var target = Fighter(2, 101, 0, 1000);
            fight.AddPlayer(caster);
            fight.AddPlayer(target);

            var outcomes = EffectEngine.ResolveEffects(
                fight, caster, 100, 1, target, EffectEngine.AlLanzar, 1,
                new[]
                {
                    FixedHeal(100, 100),
                    new SpellEffect
                    {
                        EffectId = EffectSupport.HealPercent,
                        EffectUid = 4,
                        DiceNum = 50,
                        Triggers = EffectEngine.AlLanzar,
                    },
                });

            Assert.Empty(outcomes);
            Assert.Equal(0, target.CurrentHP);
        }

        private static Fighter Fighter(long id, int cell, int currentHp, int maxHp)
            => new Fighter { Id = id, CellId = cell, CurrentHP = currentHp, MaxHP = maxHp };

        private static SpellEffect FixedHeal(int diceMin, int diceMax, string targetMask = "")
            => new SpellEffect
            {
                EffectId = EffectSupport.FireHeal,
                EffectUid = 1,
                DiceNum = diceMin,
                DiceSide = diceMax,
                Triggers = EffectEngine.AlLanzar,
                TargetMask = targetMask,

                // Fire, and it has to be said rather than left at the default. Effect 108 declares
                // effectElement 2 in all 751 of its occurrences in SpellLevels, and the element is
                // what picks the characteristic that scales the heal. Leaving it at 0 would mean
                // neutral, which scales off Strength -- a fighter shape no real 108 ever has.
                Element = 2,
            };
    }
}
