using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;
using Effect = Jondo.Unity.Server.Managers.Equipment.ItemEffect;

namespace Jondo.Unity.Tests.Economy
{
    /// <summary>
    /// Breaking items at the grinder, against the capture (Interactivos varios/"triturador-romper
    /// objetos-2 tandas"): three items, seven lines, and the coefficient the server reported for
    /// each in kfp.
    /// </summary>
    [Collection("forgemagic")]
    public class BreakingTests
    {
        private const int Initiative = 174, Chance = 123, Evasion = 752, WaterDamage = 426;
        private const int Agility = 119, Wisdom = 124, Intelligence = 126;

        private static void Runes()
        {
            Forgemagic.Declare(Initiative, 0.1);
            Forgemagic.Declare(Chance, 1);
            Forgemagic.Declare(Evasion, 4);
            Forgemagic.Declare(WaterDamage, 5);
            Forgemagic.Declare(Agility, 1);
            Forgemagic.Declare(Wisdom, 3);
            Forgemagic.Declare(Intelligence, 1);
            Breaking.Declare(
                new Forgemagic.Rune(7448, Initiative, 10), new Forgemagic.Rune(1525, Chance, 1),
                new Forgemagic.Rune(11637, Evasion, 1), new Forgemagic.Rune(11661, WaterDamage, 1),
                new Forgemagic.Rune(1524, Agility, 1), new Forgemagic.Rune(1521, Wisdom, 1),
                new Forgemagic.Rune(1522, Intelligence, 1));
        }

        private static Effect E(int effect, int value) => new Effect(effect, value, 0, 0);

        /// <summary>Always this value: 0.999 rounds every line down, 0 rounds every line up.</summary>
        private sealed class Fixed : Random
        {
            private readonly double _value;
            public Fixed(double value) => _value = value;
            public override double NextDouble() => _value;
        }

        private static void Holds(int level, Effect[] item, double coefficient, Dictionary<int, int> got)
        {
            var low = Breaking.Yield(level, item, coefficient, new Fixed(0.999));
            var high = Breaking.Yield(level, item, coefficient, new Fixed(0.0));
            foreach (int rune in low.Keys.Union(high.Keys).Union(got.Keys))
            {
                int n = got.TryGetValue(rune, out int g) ? g : 0;
                int least = low.TryGetValue(rune, out int l) ? l : 0;
                int most = high.TryGetValue(rune, out int h) ? h : 0;
                Assert.InRange(n, least, most);
            }
        }

        [Fact]
        public void The_formula_holds_on_the_seven_captured_lines()
        {
            Runes();
            // Amuleto campesino, level 20, 40.29%: one Ini and two Sue.
            Holds(20, new[] { E(Initiative, 64), E(Chance, 11), E(Evasion, 3), E(WaterDamage, 2) }, 0.40288699,
                  new Dictionary<int, int> { [7448] = 1, [1525] = 2 });
            // Capa de esponja, level 20, 65.52%: six Sue and three Agi.
            Holds(20, new[] { E(Chance, 25), E(Agility, 15) }, 0.65522200,
                  new Dictionary<int, int> { [1525] = 6, [1524] = 3 });
            // Broche Heta, level 28, 235.95%: eighteen Sa and five Inte.
            Holds(28, new[] { E(Wisdom, 17), E(Intelligence, 2) }, 2.35947490,
                  new Dictionary<int, int> { [1521] = 18, [1522] = 5 });
        }

        [Fact]
        public void The_fraction_is_the_chance_of_one_more()
        {
            Runes();
            // Wisdom 17 at level 28 and 236%: 17.63 Sa, so 17 or 18 and nothing else.
            var down = Breaking.Yield(28, new[] { E(Wisdom, 17) }, 2.3594749, new Fixed(0.64));
            var up = Breaking.Yield(28, new[] { E(Wisdom, 17) }, 2.3594749, new Fixed(0.62));
            Assert.Equal(17, down[1521]);
            Assert.Equal(18, up[1521]);
        }

        /// <summary>
        /// Focus on wisdom: the Broche Heta's wisdom keeps its 52.9 of weight and takes half of its
        /// intelligence's 4.34, all of it in Sa runes: 18.36 of them, and no Inte.
        /// </summary>
        [Fact]
        public void A_focus_turns_half_of_the_rest_into_the_focused_rune()
        {
            Runes();
            var item = new[] { E(Wisdom, 17), E(Intelligence, 2) };
            var down = Breaking.Yield(28, item, 2.3594749, new Fixed(0.999), focus: Wisdom);
            var up = Breaking.Yield(28, item, 2.3594749, new Fixed(0.0), focus: Wisdom);
            Assert.Equal(18, down[1521]);
            Assert.Equal(19, up[1521]);
            Assert.False(down.ContainsKey(1522));

            // A focus on something the item does not carry breaks it as usual.
            var elsewhere = Breaking.Yield(28, item, 2.3594749, new Fixed(0.999), focus: Chance);
            Assert.Equal(17, elsewhere[1521]);
            Assert.Equal(4, elsewhere[1522]);
        }

        [Fact]
        public void A_template_broken_loses_coefficient_and_wins_it_back()
        {
            Breaking.Forget();
            var now = DateTime.UtcNow;
            Assert.Equal(1.0, Breaking.CoefficientOf(900002, now), 9);

            Breaking.Broke(900002, 10);
            double after = Breaking.CoefficientOf(900002, now);
            Assert.Equal(Math.Pow(0.99, 10), after, 9);

            Assert.Equal(after + 2 * Breaking.RecoveryPerHour, Breaking.CoefficientOf(900002, now.AddHours(2)), 9);
            Assert.Equal(1.0, Breaking.CoefficientOf(900002, now.AddHours(40)), 9);
        }

        [Fact]
        public void Maluses_weapon_damage_and_runeless_lines_give_nothing()
        {
            Runes();
            Forgemagic.Declare(157, -0.5, bonusType: -1);
            Forgemagic.Declare(96, 2, category: 2);
            var nothing = Breaking.Yield(200, new[] { E(157, 50), new Effect(96, 0, 10, 20), E(999999, 30) }, 1, new Fixed(0));
            Assert.Empty(nothing);
            Assert.False(Breaking.Breakable(new[] { E(157, 50) }));
            Assert.True(Breaking.Breakable(new[] { E(Chance, 1) }));
        }

        // ─── The grinder's messages ─────────────────────────────────────────────────────────

        [Fact]
        public void The_grinder_takes_an_item_without_the_float()
            => Assert.Equal(Convert.FromHexString("0a1b083f2a1708c24012042019587b1204200f5877180120d7e482ff01"),
                            WorkshopProtocol.BuildAdded(8258, new[] { E(Chance, 25), E(Agility, 15) }, 1, 534819415,
                                                        withFloat: false));

        [Fact]
        public void The_break_report_carries_each_item_its_runes_and_its_coefficient()
        {
            float coefficient = BitConverter.ToSingle(Convert.FromHexString("3447ce3e"));
            byte[] built = WorkshopProtocol.BuildBroken(new[]
            {
                (534819413L, (IReadOnlyList<(int, int)>)new List<(int, int)> { (7448, 1), (1525, 2) }, coefficient),
            });
            // The real one writes the coefficient a hair apart in f5 (3d instead of 34): the
            // same 40.29% read twice. Everything up to it is the same byte for byte.
            byte[] captured = Convert.FromHexString("0a1e08d5e482ff011a0508983a10011a0508f50b1002253447ce3e2d3d47ce3e");
            Assert.Equal(captured.Length, built.Length);
            Assert.Equal(captured.Take(captured.Length - 4), built.Take(built.Length - 4));
        }
    }
}
