using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class BreedStatCostTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(99, 99)]
    [InlineData(100, 100)]
    [InlineData(101, 102)]
    [InlineData(200, 300)]
    [InlineData(300, 600)]
    [InlineData(400, 1000)]
    [InlineData(401, 1004)]
    [InlineData(int.MaxValue, 8_589_933_988L)]
    public void All_breeds_and_missing_data_share_consistent_band_costs(int points, long expected)
    {
        foreach (int breed in Enumerable.Range(1, 21).Append(int.MaxValue))
        foreach (string stat in new[] { "strength", "intelligence", "chance", "agility" })
            Assert.Equal(expected, BreedStatCost.CostOf(breed, stat, points));
    }

    [Theory]
    [InlineData("strength", 100, 100, true)]
    [InlineData("strength", 101, 100, false)]
    [InlineData("strength", 102, 101, true)]
    [InlineData("wisdom", 15, 5, true)]
    [InlineData("wisdom", 16, 5, false)]
    [InlineData("vitality", int.MaxValue, int.MaxValue, true)]
    public void Buying_inverts_the_same_price_without_overflow_or_partial_charges(string stat, int capital, int points, bool exact)
    {
        Assert.Equal(exact, BreedStatCost.TryBuy(8, stat, capital, out int actual));
        Assert.Equal(points, actual);
        Assert.True(BreedStatCost.CostOf(8, stat, actual) <= capital);
    }
}
