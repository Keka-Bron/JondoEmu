using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Managers;
using Xunit;
using Effect = Jondo.Unity.Server.Managers.Equipment.ItemEffect;

namespace Jondo.Unity.Tests.Economy
{
    /// <summary>
    /// Smithmagic: the odds of the model and the arithmetic of the pool, the latter against the
    /// two magus sessions captured (Oficios/"envio invitacion a maguear ...").
    /// </summary>
    [Collection("forgemagic")]
    public class ForgemagicTests
    {
        private const int Strength = 118, Vitality = 125, FireResistance = 430, StrengthMalus = 157;
        private const int AP = 111, MP = 128, Range = 117, WeaponDamage = 96;

        private static void Weights()
        {
            Forgemagic.Declare(Strength, 1, opposite: StrengthMalus);
            Forgemagic.Declare(StrengthMalus, -0.5, opposite: Strength, bonusType: -1);
            Forgemagic.Declare(Vitality, 0.2);
            Forgemagic.Declare(FireResistance, 5);
            Forgemagic.Declare(AP, 100, useDice: false);
            Forgemagic.Declare(MP, 90, useDice: false);
            Forgemagic.Declare(Range, 51, useDice: false);
            Forgemagic.Declare(WeaponDamage, 1, category: 2);
        }

        private static Effect E(int effect, int value) => new Effect(effect, value, 0, 0);

        private static Forgemagic.Template Template(params (int Effect, int Min, int Max)[] lines)
            => new Forgemagic.Template
            {
                Gid = 900001, Level = 10, Type = 11,
                Lines = lines.Select(l => new Forgemagic.TemplateLine(l.Effect, l.Min, l.Max, 0)).ToList(),
            };

        /// <summary>Item 8229 of the captured session: 16 to 20 strength and 16 to 20 vitality.</summary>
        private static Forgemagic.Template Boots => Template((Strength, 16, 20), (Vitality, 16, 20));

        /// <summary>A Random that answers what the test says, then 0.5 and 0.</summary>
        private sealed class Scripted : Random
        {
            private readonly Queue<double> _doubles;
            public Scripted(params double[] doubles) => _doubles = new Queue<double>(doubles);
            public override double NextDouble() => _doubles.Count > 0 ? _doubles.Dequeue() : 0.5;
            public override int Next(int maxValue) => 0;
            public override int Next(int minValue, int maxValue) => minValue;
        }

        [Fact]
        public void The_weights_are_the_clients_own()
        {
            if (!File.Exists(Paths.EffectWeightsJson)) return;
            Forgemagic.Initialize();
            Assert.Equal(0.2, Forgemagic.WeightOf(Vitality), 6);
            Assert.Equal(1, Forgemagic.WeightOf(Strength), 6);
            Assert.Equal(100, Forgemagic.WeightOf(AP), 6);
            Assert.Equal(90, Forgemagic.WeightOf(MP), 6);
            Assert.Equal(-0.5, Forgemagic.WeightOf(StrengthMalus), 6);
            Assert.True(Forgemagic.InfoOf(StrengthMalus).IsMalus);
            Assert.Equal(StrengthMalus, Forgemagic.InfoOf(Strength).Opposite);
            Assert.True(Forgemagic.InfoOf(WeaponDamage).IsWeaponDamage);
        }

        /// <summary>The community's three anchors: a weak item, a perfect jet, and the floor past it.</summary>
        [Fact]
        public void The_odds_go_from_the_weak_item_to_the_floor()
        {
            Weights();
            var weak = Forgemagic.Base(Boots, new List<Effect>());
            Assert.Equal(0.66, weak.Clean, 6);
            Assert.Equal(0.00, weak.Failure, 6);

            var perfect = Forgemagic.Base(Boots, new List<Effect> { E(Strength, 20), E(Vitality, 20) });
            Assert.Equal(0.43, perfect.Clean, 6);
            Assert.Equal(0.07, perfect.Failure, 6);

            // 32 strength and 20 vitality weigh 36 on a 24 item: half again its perfect jet.
            var floor = Forgemagic.Base(Boots, new List<Effect> { E(Strength, 32), E(Vitality, 20) });
            Assert.Equal(0.15, floor.Clean, 6);
            Assert.Equal(0.35, floor.Failure, 6);
        }

        /// <summary>
        /// The reach, as the owner of the server put it: a +1 strength enters well up to 25-30, a
        /// +10 up to 100 or more, and a +1 from 99 to 100 is all but hopeless.
        /// </summary>
        [Fact]
        public void A_rune_fades_past_its_reach()
        {
            Weights();
            var belt = Template((Strength, 0, 200));
            Forgemagic.Rune Fo(int points) => new Forgemagic.Rune(1, Strength, points);

            var low = Forgemagic.OddsOf(belt, new List<Effect> { E(Strength, 25) }, Fo(1));
            Assert.Equal(Forgemagic.Base(belt, new List<Effect> { E(Strength, 25) }).Clean, low.Clean, 6);

            var hopeless = Forgemagic.OddsOf(belt, new List<Effect> { E(Strength, 99) }, Fo(1));
            Assert.True(hopeless.Clean <= 0.0101, $"+1 at 99: {hopeless.Clean:P2}");

            var ra = Forgemagic.OddsOf(belt, new List<Effect> { E(Strength, 99) }, Fo(10));
            Assert.True(ra.Clean > 0.5, $"+10 at 99: {ra.Clean:P2}");

            var pa = Forgemagic.OddsOf(belt, new List<Effect> { E(Strength, 50) }, Fo(3));
            Assert.Equal(Forgemagic.Base(belt, new List<Effect> { E(Strength, 50) }).Clean, pa.Clean, 6);
            Assert.Equal(1, hopeless.Clean + hopeless.Partial + hopeless.Failure, 6);
        }

        [Fact]
        public void An_exo_ap_is_one_percent_and_only_one_of_the_three()
        {
            Weights();
            var odds = Forgemagic.OddsOf(Boots, new List<Effect> { E(Strength, 18) }, new Forgemagic.Rune(1, AP, 1));
            Assert.Equal(0.01, odds.Clean, 9);
            Assert.Equal(0, odds.Partial, 9);
            Assert.Equal(0.99, odds.Failure, 9);

            var second = Forgemagic.OddsOf(Boots, new List<Effect> { E(MP, 1) }, new Forgemagic.Rune(1, AP, 1));
            Assert.Equal(0, second.Clean);

            var twice = Forgemagic.OddsOf(Boots, new List<Effect> { E(AP, 1) }, new Forgemagic.Rune(1, AP, 1));
            Assert.Equal(0, twice.Clean);
        }

        [Fact]
        public void Nothing_goes_over_a_weight_of_101()
        {
            Weights();
            var capped = Forgemagic.OddsOf(Boots, new List<Effect> { E(Strength, 100) }, new Forgemagic.Rune(1, Strength, 3));
            Assert.Equal(new Forgemagic.Odds(0, 0, 1), capped);

            var over = Forgemagic.OddsOf(Boots, new List<Effect> { E(Strength, 30) }, new Forgemagic.Rune(1, Strength, 3));
            var inRange = Forgemagic.OddsOf(Boots, new List<Effect> { E(Strength, 16) }, new Forgemagic.Rune(1, Strength, 3));
            Assert.True(over.Clean > 0 && over.Clean < inRange.Clean);
        }

        /// <summary>
        /// Item 13091, level 44, in the captured session: a +3 strength rune enters as a partial
        /// success and costs a point of fire resistance (5): 5 lost for a rune of 3, pool 0 -> 2,
        /// kdr f2.f1 = 1.
        /// </summary>
        [Fact]
        public void A_partial_success_pays_the_rune_and_pools_the_rest()
        {
            Weights();
            var hat = Template((Strength, 0, 30), (FireResistance, 0, 3));
            var before = new List<Effect> { E(Strength, 20), E(FireResistance, 3) };
            var odds = new Forgemagic.Odds(0, 1, 0);

            var result = Forgemagic.Resolve(hat, before, new Forgemagic.Rune(1, Strength, 3),
                                            Forgemagic.Outcome.Partial, odds, new Scripted());

            Assert.True(result.Succeeded);
            Assert.Equal(23, result.Effects.Single(e => e.Effect == Strength).Value);
            Assert.Equal(2, result.Effects.Single(e => e.Effect == FireResistance).Value);
            Assert.Equal(2.0, result.Pool, 6);
            Assert.Equal(Forgemagic.PoolChange.Up, result.PoolChange);
            Assert.Equal(200, result.Effects.Single(e => e.Effect == Forgemagic.PoolEffect).Value);
        }

        /// <summary>
        /// Item 8247 in the captured session: a Vi rune fails with 4.0 in the pool, and the pool
        /// pays it all: 4.0 -> 3.0, kdr f2.f1 = 2, and the item keeps its 43 vitality.
        /// </summary>
        [Fact]
        public void The_pool_pays_a_failure_before_the_item()
        {
            Weights();
            var before = new List<Effect> { E(Vitality, 43), new Effect(Forgemagic.PoolEffect, 400, 0, 0) };
            var result = Forgemagic.Resolve(Boots, before, new Forgemagic.Rune(1, Vitality, 5),
                                            Forgemagic.Outcome.Failure, new Forgemagic.Odds(0, 0, 1), new Scripted(0.9));

            Assert.False(result.Succeeded);
            Assert.Equal(43, result.Effects.Single(e => e.Effect == Vitality).Value);
            Assert.Equal(3.0, result.Pool, 6);
            Assert.Equal(Forgemagic.PoolChange.Down, result.PoolChange);
        }

        [Fact]
        public void A_harmless_failure_changes_nothing()
        {
            Weights();
            var before = new List<Effect> { E(Vitality, 19), E(Strength, 19) };
            var result = Forgemagic.Resolve(Boots, before, new Forgemagic.Rune(1, Vitality, 5),
                                            Forgemagic.Outcome.Failure, new Forgemagic.Odds(0, 0, 1), new Scripted(0.1));

            Assert.Equal(Forgemagic.PoolChange.Same, result.PoolChange);
            Assert.Equal(19, result.Effects.Single(e => e.Effect == Vitality).Value);
            Assert.Equal(19, result.Effects.Single(e => e.Effect == Strength).Value);
            Assert.Empty(result.Lost);
        }

        [Fact]
        public void A_failure_that_hurts_takes_the_runes_weight_off_the_item()
        {
            Weights();
            var before = new List<Effect> { E(Vitality, 19), E(Strength, 19) };
            var result = Forgemagic.Resolve(Boots, before, new Forgemagic.Rune(1, Strength, 3),
                                            Forgemagic.Outcome.Failure, new Forgemagic.Odds(0, 0, 1), new Scripted(0.9));

            double lost = result.Lost.Sum(l => l.Value * Forgemagic.WeightOf(l.Key));
            Assert.True(lost >= 3 - 1e-9, $"lost {lost}");
            Assert.Equal(lost - 3, result.Pool, 6);
        }

        [Fact]
        public void A_clean_rune_eats_the_opposite_malus_first()
        {
            Weights();
            var belt = Template((Strength, 0, 50));
            var before = new List<Effect> { E(StrengthMalus, 5) };

            var three = Forgemagic.Resolve(belt, before, new Forgemagic.Rune(1, Strength, 3),
                                           Forgemagic.Outcome.Clean, new Forgemagic.Odds(1, 0, 0), new Scripted());
            Assert.Equal(2, three.Effects.Single(e => e.Effect == StrengthMalus).Value);
            Assert.DoesNotContain(three.Effects, e => e.Effect == Strength);

            var ten = Forgemagic.Resolve(belt, before, new Forgemagic.Rune(1, Strength, 10),
                                         Forgemagic.Outcome.Clean, new Forgemagic.Odds(1, 0, 0), new Scripted());
            Assert.DoesNotContain(ten.Effects, e => e.Effect == StrengthMalus);
            Assert.Equal(5, ten.Effects.Single(e => e.Effect == Strength).Value);
        }

        /// <summary>
        /// A signature and the pool live in the item's own effects, so they go wherever the item
        /// goes: stored, read back, the text and the pool are still there.
        /// </summary>
        [Fact]
        public void The_signature_and_the_pool_travel_with_the_item()
        {
            Weights();
            var effects = new List<Effect>
            {
                E(Strength, 12), new Effect(Forgemagic.PoolEffect, 180, 0, 0),
                new Effect(Forgemagic.ModifiedBy, 0, 0, 0, "Otro"),
            };
            var signed = Forgemagic.Signed(effects, Forgemagic.ModifiedBy, "Sacri-Master");
            Assert.Single(signed, e => e.Effect == Forgemagic.ModifiedBy);

            var read = Equipment.ParseEffects(Forgemagic.Serialize(signed));
            Assert.Equal("Sacri-Master", read.Single(e => e.Effect == Forgemagic.ModifiedBy).Text);
            Assert.Equal(1.8, Forgemagic.PoolOf(read), 6);
            Assert.Equal(12, read.Single(e => e.Effect == Strength).Value);
        }

        [Fact]
        public void A_crafted_item_rolls_its_characteristics_and_keeps_its_weapon_damage()
        {
            Weights();
            var dagger = Template((Strength, 16, 20), (WeaponDamage, 5, 9));
            var rolled = Forgemagic.Roll(dagger, new Random(7));

            var strength = rolled.Single(e => e.Effect == Strength);
            Assert.InRange(strength.Value, 16, 20);
            Assert.Equal(0, strength.DiceNum);
            var damage = rolled.Single(e => e.Effect == WeaponDamage);
            Assert.Equal((0L, 5L, 9L), (damage.Value, damage.DiceNum, damage.DiceSide));

            Assert.False(Forgemagic.Stacks(dagger));
            Assert.True(Forgemagic.Stacks(Template((Vitality, 5, 5))));
        }

        [Fact]
        public void A_roll_under_the_clean_odds_is_a_clean_success()
        {
            Weights();
            var result = Forgemagic.Apply(Boots, new List<Effect> { E(Strength, 16) },
                                          new Forgemagic.Rune(1, Strength, 1), new Scripted(0.0));
            Assert.Equal(Forgemagic.Outcome.Clean, result.Outcome);
            Assert.Equal(17, result.Effects.Single(e => e.Effect == Strength).Value);
        }
    }
}
