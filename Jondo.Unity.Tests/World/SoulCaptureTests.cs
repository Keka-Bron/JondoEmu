using System;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>Soul capture: the stone's 705, the level of the strongest soul, and the full stones.</summary>
    public class SoulCaptureTests
    {
        private static readonly Equipment.ItemEffect[] Small =
        {
            new(SoulCapture.PowerEffect, 50, 100, 0),
            new(1175, 0, 21714, 1),
        };

        [Fact]
        public void A_stone_says_its_power()
        {
            Assert.Equal((50, 100), SoulCapture.PowerOf(Small));
            Assert.Equal((0, 0), SoulCapture.PowerOf(new[] { new Equipment.ItemEffect(1175, 0, 21714, 1) }));
        }

        /// <summary>«une pierre ne peut capturer un groupe que si son niveau dépasse celui du monstre le plus fort».</summary>
        [Fact]
        public void A_stone_holds_souls_up_to_its_level()
        {
            var group = new[]
            {
                new SoulCapture.Soul(101, 3, 40, false, false),
                new SoulCapture.Soul(102, 5, 50, false, false),
            };
            var stones = SoulCapture.Capture(group, 50, 100, new Random(1));
            var full = Assert.Single(stones);
            Assert.Equal(SoulCapture.FullStone, full.Item);
            Assert.Equal(new[] { (623, 101L, 3L), (623, 102L, 5L) }, full.Effects.Select(e => (e.Effect, e.Value, e.DiceSide)));

            Assert.Empty(SoulCapture.Capture(group, 49, 100, new Random(1)));
            Assert.Empty(SoulCapture.Capture(group, 50, 0, new Random(1)));
            Assert.Empty(SoulCapture.Capture(Array.Empty<SoulCapture.Soul>(), 50, 100, new Random(1)));
        }

        /// <summary>Each archmonster in a stone of its own; the guardian's stone holds the rest of its group.</summary>
        [Fact]
        public void Archmonsters_go_apart_and_the_guardian_takes_its_group()
        {
            var group = new[]
            {
                new SoulCapture.Soul(201, 1, 100, true, false),
                new SoulCapture.Soul(202, 2, 120, false, true),
                new SoulCapture.Soul(203, 3, 90, false, false),
                new SoulCapture.Soul(204, 4, 95, false, false),
            };
            var stones = SoulCapture.Capture(group, 150, 100, new Random(2));
            Assert.Equal(new[] { SoulCapture.ArchmonsterStone, SoulCapture.GuardianStone }, stones.Select(s => s.Item));
            Assert.Equal(new[] { 201 }, stones[0].Souls.Select(s => s.Monster));
            Assert.Equal(new[] { 202, 203, 204 }, stones[1].Souls.Select(s => s.Monster));
        }
    }
}
