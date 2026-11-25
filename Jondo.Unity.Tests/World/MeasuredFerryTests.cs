using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The NPCs who carry you across for kamas talk on each quay as the official server talked there:
    /// one tree for both quays offered, on each, the other quay's paid reply, which charged and led
    /// nowhere (Capitã Isabella Utissier in Albuera, found in play 2026-11-23).
    /// </summary>
    public sealed class MeasuredFerryTests
    {
        /// <summary>Each quay opens on its own line, and offers only its own way across.</summary>
        [Theory]
        [InlineData(6801, 68421124, 51204, 68743, 69451)]   // Madrestam: pay 100 and embark
        [InlineData(6801, 223347712, 51205, 69451, 68743)]  // Albuera: the way back
        [InlineData(3464, 68422145, 25721, 30858, 30854)]   // Madrestam: 500 to Saarrakech
        [InlineData(3464, 173277701, 25718, 30854, 30858)]  // Saarrakech: the way back
        [InlineData(6530, 90705410, 49576, 66179, 66178)]
        [InlineData(6530, 212864519, 49576, 66178, 66179)]
        public void Each_quay_offers_its_own_crossing(int npc, long map, int opening, int offered, int notOffered)
        {
            NpcDialogues.Load();
            var tree = NpcDialogues.For(npc, map);
            Assert.NotNull(tree);
            Assert.Equal(opening, tree!.Opening);
            var line = tree.Line(opening);
            Assert.NotNull(line);
            Assert.NotNull(line!.Choice(offered));
            Assert.Null(line.Choice(notOffered));
        }

        /// <summary>The ferrymen whose first line asks first: the other quay's line is not in this tree.</summary>
        [Theory]
        [InlineData(6587, 68423176, 57863)]
        [InlineData(6587, 212730373, 57864)]
        [InlineData(2924, 212865031, 57338)]
        [InlineData(2924, 68422662, 49574)]
        public void The_other_quays_line_is_not_said_here(int npc, long map, int otherLine)
        {
            NpcDialogues.Load();
            var tree = NpcDialogues.For(npc, map);
            Assert.NotNull(tree);
            Assert.Null(tree!.Line(otherLine));
        }

        /// <summary>Mago Lhanz (1601): «Previsão do Mago Lhanz» (17810) for 1 kama, as measured.</summary>
        [Fact]
        public void Mago_Lhanz_sells_his_forecast()
        {
            NpcDialogues.Load();
            var c = NpcDialogues.For(1601, 90703360)?.Line(23641)?.Choice(28391);
            Assert.NotNull(c);
            Assert.Equal((17810, 1L), (c!.BuysItem, c.BuysPrice));
        }
    }
}
