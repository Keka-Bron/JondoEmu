using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The purses' kamas are their effect 194 (ItemUseHandler.KamasEffect), which nothing
    /// handled: a use answered «not handled yet» and did nothing.
    /// </summary>
    public class KamaPurseTests
    {
        [Theory]
        [InlineData(8815, 1000)]
        [InlineData(8814, 5000)]
        [InlineData(8425, 10000)]
        public void A_purse_holds_its_kamas_in_effect_194(int purse, int kamas)
        {
            var effects = DatabaseManager.GetItemEffectsData(DatabaseManager.GetItemTemplatePossibleEffects(purse));
            if (effects.Count == 0) return;   // no item data

            var row = Assert.Single(effects, e => e.EffectId == ItemUseHandler.KamasEffect);
            Assert.Equal(kamas, row.DiceNum > 0 ? row.DiceNum : row.Value);
            Assert.True(row.DiceSide <= row.DiceNum, "a fixed amount, not a range");
        }
    }
}
