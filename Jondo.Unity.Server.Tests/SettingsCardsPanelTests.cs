using Jondo.Unity.Server.UI;
using Xunit;

namespace Jondo.Unity.Server.Tests;

/// <summary>How the settings cards are spread over the columns.</summary>
public sealed class SettingsCardsPanelTests
{
    /// <summary>
    /// The eight cards' compact heights as Windows' fonts draw them at 1264 x 713: in order, the
    /// last small card lands on the loot card's column and ends at 515 of a 498 window; spread
    /// better, the loot card stands alone and every column fits.
    /// </summary>
    [Fact]
    public void A_better_spread_fits_where_the_order_does_not()
    {
        double[] heights = { 213, 364, 183, 252, 241, 160, 137, 137 };
        var inOrder = SettingsCardsPanel.InOrder(heights, 4);
        Assert.Equal(515, SettingsCardsPanel.Tallest(heights, inOrder, 4));

        var spread = SettingsCardsPanel.Shortest(heights, 4, inOrder);
        Assert.True(SettingsCardsPanel.Tallest(heights, spread, 4) <= 498);
        Assert.Equal(0, spread[0]);
        Assert.Single(spread, c => c == spread[1]);
    }

    [Fact]
    public void The_search_never_does_worse_than_the_order()
    {
        var dice = new Random(7);
        for (int round = 0; round < 50; round++)
        {
            var heights = Enumerable.Range(0, 8).Select(_ => (double)dice.Next(80, 400)).ToArray();
            for (int columns = 1; columns <= 5; columns++)
            {
                var inOrder = SettingsCardsPanel.InOrder(heights, columns);
                var spread = SettingsCardsPanel.Shortest(heights, columns, inOrder);
                Assert.True(SettingsCardsPanel.Tallest(heights, spread, columns)
                            <= SettingsCardsPanel.Tallest(heights, inOrder, columns));
                Assert.All(spread, c => Assert.InRange(c, 0, columns - 1));
            }
        }
    }
}
