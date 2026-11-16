using System.IO;
using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Economy
{
    /// <summary>
    /// Prospecting on the loot: the client's own multiplier (Lua 106 of its luaformulasdataroot),
    /// from ×1 at a hundred to the ×2.5 cap, and the rows that say it does not apply.
    /// </summary>
    public class ProspectingLootTests
    {
        [Theory]
        [InlineData(100, 1.0)]
        [InlineData(0, 1.0)]
        [InlineData(-500, 1.0)]
        [InlineData(400, 2.2384)]
        [InlineData(600, 2.4672)]
        [InlineData(1_000_114, 2.495)]
        public void The_multiplier_is_the_clients_curve(int prospecting, double expected)
            => Assert.Equal(expected, FightHandler.DropMultiplier(prospecting), 3);

        [Fact]
        public void It_only_grows_and_never_passes_the_cap()
        {
            double before = 0;
            for (int pp = 100; pp <= 5000; pp += 25)
            {
                double now = FightHandler.DropMultiplier(pp);
                Assert.True(now >= before, $"{pp}: {now} < {before}");
                Assert.InRange(now, 1.0, 2.5);
                before = now;
            }
        }

        /// <summary>
        /// A million prospecting on a 1 % row: what the client's tooltip showed, 2.62 %, is this
        /// times the 1.05 it adds of its own (BestiaryTabUI.GetFinalDropCoeff).
        /// </summary>
        [Fact]
        public void A_million_prospecting_is_the_tooltips_figure()
            => Assert.Equal(2.62, 1.0 * FightHandler.DropMultiplier(1_000_114) * 1.05, 2);

        [Fact]
        public void A_row_that_disables_the_modificator_is_read_so()
        {
            if (!File.Exists(Paths.WorldDb)) return;
            // Monster 102's 32706: «disableDropModificator» 1 in the client's data.
            var drops = DatabaseManager.GetMonsterDrops(102, 0);
            var row = drops.FirstOrDefault(d => d.ObjectId == 32706);
            if (row == null) return;
            Assert.True(row.NoProspecting);
            Assert.Contains(drops, d => !d.NoProspecting);
        }
    }
}
