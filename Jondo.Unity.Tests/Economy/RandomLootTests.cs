using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;
using Rarity = Jondo.Unity.Server.Managers.RandomLoot.Rarity;

namespace Jondo.Unity.Tests.Economy
{
    /// <summary>
    /// The server's random loot: two to five items around a beaten monster's level, by rarity,
    /// and once in two thousand a perfect piece with an exo AP or MP.
    /// </summary>
    [Collection("forgemagic")]
    public class RandomLootTests
    {
        private const int Strength = 118, StrengthMalus = 157;

        // Item types of the client's data: a hat, a potion, a pet, a dofus, a cosmetic hat, a trophy.
        private const int Hat = 16, Potion = 12, Pet = 18, Dofus = 23, CosmeticHat = 246, Trophy = 151;

        [Theory]
        [InlineData(0.0, Rarity.Perfect)]
        [InlineData(0.049, Rarity.Perfect)]
        [InlineData(0.05, Rarity.Legendary)]
        [InlineData(0.549, Rarity.Legendary)]
        [InlineData(0.55, Rarity.Dofus)]
        [InlineData(5.549, Rarity.Dofus)]
        [InlineData(5.55, Rarity.Creature)]
        [InlineData(15.549, Rarity.Creature)]
        [InlineData(15.55, Rarity.Cosmetic)]
        [InlineData(30.549, Rarity.Cosmetic)]
        [InlineData(30.55, Rarity.Equipment)]
        [InlineData(55.549, Rarity.Equipment)]
        [InlineData(55.55, Rarity.Consumable)]
        [InlineData(99.99, Rarity.Consumable)]
        public void Each_roll_lands_on_its_rarity(double roll, Rarity rarity)
            => Assert.Equal(rarity, RandomLoot.RarityFor(roll));

        /// <summary>Something to wear more often than not: 55.55 % against 44.45 % of consumables.</summary>
        [Fact]
        public void Equipment_comes_before_consumables()
        {
            double worn = RandomLoot.Odds.Sum(o => o.Percent);
            Assert.Equal(55.55, worn, 6);
            Assert.True(worn > 100 - worn);
        }

        [Fact]
        public void Levels_fit_by_rarity()
        {
            Assert.True(RandomLoot.Fits(Rarity.Equipment, 110, 100));
            Assert.True(RandomLoot.Fits(Rarity.Equipment, 90, 100));
            Assert.False(RandomLoot.Fits(Rarity.Equipment, 111, 100));
            Assert.False(RandomLoot.Fits(Rarity.Consumable, 89, 100));

            // Dofus come in steps, so twenty levels either way: a level-160 monster reaches the 180s.
            Assert.True(RandomLoot.Fits(Rarity.Dofus, 180, 160));
            Assert.True(RandomLoot.Fits(Rarity.Dofus, 140, 160));
            Assert.False(RandomLoot.Fits(Rarity.Dofus, 181, 160));

            // Mounts, pets and cosmetics do not grow with level: anything up to ten above.
            Assert.True(RandomLoot.Fits(Rarity.Creature, 20, 150));
            Assert.True(RandomLoot.Fits(Rarity.Cosmetic, 1, 200));
            Assert.False(RandomLoot.Fits(Rarity.Creature, 60, 49));
        }

        /// <summary>The families come from the client's item types and flags.</summary>
        [Fact]
        public void Items_take_their_rarity_from_the_client_s_data()
        {
            Assert.Equal(Rarity.Equipment, RandomLoot.RarityOf(Hat, 1024));
            Assert.Equal(Rarity.Legendary, RandomLoot.RarityOf(Hat, 2048));
            Assert.Null(RandomLoot.RarityOf(Hat, 16));                       // ethereal: it breaks
            Assert.Equal(Rarity.Cosmetic, RandomLoot.RarityOf(CosmeticHat, 0));
            Assert.Equal(Rarity.Creature, RandomLoot.RarityOf(Pet, 0));
            Assert.Equal(Rarity.Dofus, RandomLoot.RarityOf(Dofus, 0));
            Assert.Equal(Rarity.Consumable, RandomLoot.RarityOf(Potion, 1 | 1024));
            Assert.Null(RandomLoot.RarityOf(Potion, 1));                     // usable but not for sale
            Assert.Null(RandomLoot.RarityOf(Trophy, 0));

            Assert.True(RandomLoot.IsWorn(Hat));
            Assert.True(RandomLoot.IsWorn(Dofus));
            Assert.True(RandomLoot.IsWorn(Trophy));
            Assert.True(RandomLoot.IsWorn(CosmeticHat));
            Assert.False(RandomLoot.IsWorn(Potion));
        }

        /// <summary>The database's items: legendaries all at level 200, and something of every rarity.</summary>
        [Fact]
        public void The_database_has_something_of_every_rarity()
        {
            RandomLoot.Forget();
            string summary = RandomLoot.Summary();
            foreach (var rarity in new[] { Rarity.Consumable, Rarity.Equipment, Rarity.Cosmetic, Rarity.Creature,
                                           Rarity.Dofus, Rarity.Legendary })
                Assert.Matches($@"\b[1-9]\d* {rarity}\b", summary);
            Assert.Contains("25 Legendary", summary);
        }

        [Fact]
        public void Nothing_drops_at_no_chance_and_two_to_five_at_certainty()
        {
            UseSmallWorld();
            var dice = new Random(7);
            Assert.Empty(RandomLoot.Roll(100, 0, dice));
            for (int i = 0; i < 200; i++)
            {
                var drops = RandomLoot.Roll(100, 100, dice);
                Assert.InRange(drops.Count, RandomLoot.FewestItems, RandomLoot.MostItems);
            }
        }

        /// <summary>A rarity with nothing at the monster's level drops ordinary equipment, and that a consumable.</summary>
        [Fact]
        public void An_empty_rarity_falls_back_to_equipment_and_then_to_consumables()
        {
            RandomLoot.Use(new[] { new RandomLoot.Candidate(900101, 100, Rarity.Equipment) });
            var dice = new Random(3);
            for (int i = 0; i < 500; i++)
            {
                var drop = RandomLoot.Pick(100, dice);
                Assert.NotNull(drop);
                Assert.Equal(900101, drop.Value.Gid);
            }

            RandomLoot.Use(new[] { new RandomLoot.Candidate(900102, 100, Rarity.Consumable) });
            for (int i = 0; i < 500; i++) Assert.Equal(900102, RandomLoot.Pick(100, dice)?.Gid);

            // Far from every level, nothing.
            Assert.Null(RandomLoot.Pick(30, dice));
            RandomLoot.Forget();
        }

        /// <summary>Over many items the rarities come out at their odds.</summary>
        [Fact]
        public void The_rarities_come_out_at_their_odds()
        {
            UseSmallWorld();
            var dice = new Random(11);
            var counts = new Dictionary<Rarity, int>();
            const int picks = 200_000;
            for (int i = 0; i < picks; i++)
            {
                var drop = RandomLoot.Pick(100, dice).Value;
                counts.TryGetValue(drop.Rarity, out int n);
                counts[drop.Rarity] = n + 1;
            }
            double Share(Rarity r) => 100.0 * counts.GetValueOrDefault(r) / picks;
            Assert.InRange(Share(Rarity.Equipment), 24.5, 25.5);
            Assert.InRange(Share(Rarity.Cosmetic), 14.5, 15.5);
            Assert.InRange(Share(Rarity.Creature), 9.6, 10.4);
            Assert.InRange(Share(Rarity.Dofus), 4.7, 5.3);
            Assert.InRange(Share(Rarity.Legendary), 0.4, 0.6);
            Assert.InRange(Share(Rarity.Perfect), 0.02, 0.09);
            Assert.InRange(Share(Rarity.Consumable), 43.9, 45.0);
            RandomLoot.Forget();
        }

        /// <summary>
        /// A perfect piece: every line at its best -- the top for a bonus, the bottom for a malus --
        /// and the AP or MP its template does not roll, as its exo.
        /// </summary>
        [Fact]
        public void A_perfect_piece_has_every_line_at_its_best_and_an_exo()
        {
            Forgemagic.Declare(Strength, 1);
            Forgemagic.Declare(StrengthMalus, -1, bonusType: -1);
            Forgemagic.Declare(Forgemagic.ActionPoints, 100);
            Forgemagic.Declare(Forgemagic.MovementPoints, 90);
            var hat = new Forgemagic.Template
            {
                Gid = 900201, Level = 100, Type = Hat,
                Lines = new[]
                {
                    new Forgemagic.TemplateLine(Strength, 20, 40, 0),
                    new Forgemagic.TemplateLine(StrengthMalus, 5, 10, 0),
                    new Forgemagic.TemplateLine(Forgemagic.ActionPoints, 1, 1, 0),
                },
            };
            Forgemagic.Declare(hat);
            RandomLoot.Use(new[] { new RandomLoot.Candidate(hat.Gid, 100, Rarity.Equipment) });

            // The only roll a perfect piece can carry: MP, since the hat already rolls its AP.
            var dice = new Random(5);
            RandomLoot.Drop? perfect = null;
            for (int i = 0; i < 100_000 && perfect == null; i++)
            {
                var drop = RandomLoot.Pick(100, dice);
                if (drop?.Rarity == Rarity.Perfect) perfect = drop;
            }
            Assert.NotNull(perfect);
            Assert.Equal(Forgemagic.MovementPoints, perfect.Value.Exo);

            var effects = RandomLoot.EffectsOf(hat, perfect.Value.Exo, dice);
            Assert.Equal(40, effects.Single(e => e.Effect == Strength).Value);
            Assert.Equal(5, effects.Single(e => e.Effect == StrengthMalus).Value);
            Assert.Equal(1, effects.Single(e => e.Effect == Forgemagic.ActionPoints).Value);
            Assert.Equal(1, effects.Single(e => e.Effect == Forgemagic.MovementPoints).Value);

            // An ordinary piece rolls in its range, with no exo.
            for (int i = 0; i < 50; i++)
            {
                var rolled = RandomLoot.EffectsOf(hat, 0, dice);
                Assert.InRange(rolled.Single(e => e.Effect == Strength).Value, 20, 40);
                Assert.DoesNotContain(rolled, e => e.Effect == Forgemagic.MovementPoints);
            }
            RandomLoot.Forget();
        }

        /// <summary>One item of every rarity at level 100, whose equipment can be perfect.</summary>
        private static void UseSmallWorld()
        {
            Forgemagic.Declare(new Forgemagic.Template
            {
                Gid = 900001, Level = 100, Type = Hat,
                Lines = new[] { new Forgemagic.TemplateLine(Strength, 20, 40, 0) },
            });
            RandomLoot.Use(new[]
            {
                new RandomLoot.Candidate(900001, 100, Rarity.Equipment),
                new RandomLoot.Candidate(900002, 200, Rarity.Legendary),
                new RandomLoot.Candidate(900003, 100, Rarity.Legendary),
                new RandomLoot.Candidate(900004, 100, Rarity.Dofus),
                new RandomLoot.Candidate(900005, 20, Rarity.Creature),
                new RandomLoot.Candidate(900006, 1, Rarity.Cosmetic),
                new RandomLoot.Candidate(900007, 95, Rarity.Consumable),
            });
        }
    }
}
