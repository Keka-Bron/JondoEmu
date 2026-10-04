using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;
using Effect = Jondo.Unity.Server.Managers.Equipment.ItemEffect;

namespace Jondo.Unity.Tests.Economy
{
    /// <summary>
    /// The Infinite Dreams' transcendence runes (item type 211): 100% of success, "Ninguna
    /// forjamagia futura" afterwards, and the density rule the community's table follows.
    /// </summary>
    [Collection("forgemagic")]
    public class TranscendenceTests
    {
        private const int Strength = 118, Vitality = 125, Crit = 115;

        private static void Weights()
        {
            Forgemagic.Declare(Strength, 1);
            Forgemagic.Declare(Vitality, 0.2);
            Forgemagic.Declare(Crit, 10);
            Forgemagic.Declare(Forgemagic.NoMoreSmithmagic, 0, category: 4, useDice: false);
        }

        private static Effect E(int effect, int value) => new Effect(effect, value, 0, 0);

        /// <summary>A transcendence rune's template, the way ItemEffects keeps Runa Ta Fu (20558).</summary>
        private static Forgemagic.Transcendence Rune(int gid, int effect, int points, int density)
        {
            Forgemagic.Declare(new Forgemagic.Template
            {
                Gid = gid, Level = 104, Type = Forgemagic.TranscendenceType,
                Lines = new List<Forgemagic.TemplateLine>
                {
                    new(Forgemagic.NoMoreSmithmagic, 0, 0, 0),
                    new(Forgemagic.SuccessChance, 100, 100, 100),
                    new(Forgemagic.TranscendenceDensity, density, density, density),
                    new(effect, points, points, 0),
                },
            });
            return Forgemagic.TranscendenceOf(gid)!.Value;
        }

        private static Forgemagic.Template Belt => new Forgemagic.Template
        {
            Gid = 900010, Level = 150, Type = 10,
            Lines = new List<Forgemagic.TemplateLine> { new(Strength, 50, 80, 0), new(Vitality, 200, 300, 0) },
        };

        [Fact]
        public void The_template_says_what_the_rune_is()
        {
            Weights();
            var ta = Rune(900020, Strength, 10, 40);
            Assert.Equal((Strength, 10, 40, 100), (ta.Effect, ta.Points, ta.Density, ta.Chance));
            Assert.Null(Forgemagic.RuneOf(900020));
        }

        /// <summary>The community's table: Ta, Buta and Suta strength take a line of 61, 41 and 21 at most.</summary>
        [Fact]
        public void The_line_may_weigh_101_minus_the_density()
        {
            Weights();
            var belt = new Forgemagic.Template
            {
                Gid = 900011, Level = 150, Type = 10,
                Lines = new List<Forgemagic.TemplateLine> { new(Strength, 50, 90, 0) },
            };
            var ta = Rune(900021, Strength, 10, 40);
            var buta = Rune(900022, Strength, 15, 60);
            var suta = Rune(900023, Strength, 20, 80);

            Assert.Null(Forgemagic.TranscendenceRefusal(belt, new[] { E(Strength, 61) }, ta));
            Assert.NotNull(Forgemagic.TranscendenceRefusal(belt, new[] { E(Strength, 62) }, ta));
            Assert.Null(Forgemagic.TranscendenceRefusal(belt, new[] { E(Strength, 41) }, buta));
            Assert.NotNull(Forgemagic.TranscendenceRefusal(belt, new[] { E(Strength, 42) }, buta));
            Assert.Null(Forgemagic.TranscendenceRefusal(belt, new[] { E(Strength, 21) }, suta));
            Assert.NotNull(Forgemagic.TranscendenceRefusal(belt, new[] { E(Strength, 22) }, suta));

            // A line the item does not have at all takes any of them.
            var crit = Rune(900024, Crit, 1, 40);
            Assert.Null(Forgemagic.TranscendenceRefusal(belt, new[] { E(Strength, 90) }, crit));
        }

        [Fact]
        public void Never_on_an_over_an_exo_or_twice()
        {
            Weights();
            var ta = Rune(900025, Vitality, 50, 40);
            Assert.NotNull(Forgemagic.TranscendenceRefusal(Belt, new[] { E(Strength, 81) }, ta));          // over
            Assert.NotNull(Forgemagic.TranscendenceRefusal(Belt, new[] { E(Strength, 60), E(Crit, 1) }, ta)); // exo
            Assert.NotNull(Forgemagic.TranscendenceRefusal(Belt,
                new[] { E(Strength, 60), E(Forgemagic.NoMoreSmithmagic, 0) }, ta));                          // twice
            Assert.Null(Forgemagic.TranscendenceRefusal(Belt, new[] { E(Strength, 80), E(Vitality, 300) }, ta));
        }

        [Fact]
        public void It_goes_on_whole_and_closes_the_item()
        {
            Weights();
            var ta = Rune(900026, Strength, 10, 40);
            var before = new List<Effect> { E(Strength, 61), E(Vitality, 250), new Effect(Forgemagic.PoolEffect, 120, 0, 0) };
            var result = Forgemagic.Transcend(before, ta);

            Assert.Equal(Forgemagic.Outcome.Clean, result.Outcome);
            Assert.Equal(71, result.Effects.Single(e => e.Effect == Strength).Value);
            Assert.Equal(250, result.Effects.Single(e => e.Effect == Vitality).Value);
            Assert.Contains(result.Effects, e => e.Effect == Forgemagic.NoMoreSmithmagic);
            Assert.Equal(1.2, result.Pool, 6);
            Assert.Equal(Forgemagic.PoolChange.Same, result.PoolChange);
        }
    }
}
