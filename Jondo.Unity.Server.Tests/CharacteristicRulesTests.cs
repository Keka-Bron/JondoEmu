using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class CharacteristicRulesTests
{
    [Theory]
    [InlineData(int.MinValue, 55, 0)]
    [InlineData(0, 55, 0)]
    [InlineData(1, 55, 0)]
    [InlineData(2, 60, 5)]
    [InlineData(199, 1045, 990)]
    [InlineData(200, 1050, 995)]
    [InlineData(201, 1050, 995)]
    [InlineData(354, 1050, 995)]
    [InlineData(int.MaxValue, 1050, 995)]
    public void Shared_life_and_capital_rules_stop_at_the_normal_level_cap(int level, int life, int capital)
    {
        Assert.Equal(life, CharacteristicRules.BaseLifeForLevel(level));
        Assert.Equal(capital, CharacteristicRules.TotalCapitalForLevel(level));
        Assert.Equal(Math.Min(level, 200), CharacteristicRules.StatLevel(level));
    }

    [Theory]
    [InlineData(1, 6)]
    [InlineData(99, 6)]
    [InlineData(100, 7)]
    [InlineData(200, 7)]
    [InlineData(354, 7)]
    public void Combat_keeps_the_confirmed_six_then_seven_innate_AP_rule(int level, int expected)
    {
        var session = GameSession.SinSocket();
        session.State.CharacterLevel = level;
        using var context = SessionContext.Push(session);
        Assert.Equal(expected, session.Combat.PlayerInnateAp());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    [InlineData(101, 102)]
    [InlineData(120, 140)]
    [InlineData(200, 300)]
    [InlineData(300, 600)]
    [InlineData(400, 1000)]
    [InlineData(401, 1004)]
    [InlineData(int.MaxValue, 8_589_933_988L)]
    public void Preset_elemental_prices_use_breed_tiers_without_int32_wraparound(int strength, long expected)
        => Assert.Equal(expected, CharacteristicRules.ComputeDistributionCost(strength, 0, 0, 0, 0, 0));

    [Fact]
    public void Large_multiplications_and_combined_costs_remain_above_the_available_capital()
    {
        Assert.Equal(4_294_967_298L, CharacteristicRules.ComputeDistributionCost(0, 0, 0, 0, 0, 1_431_655_766));
        Assert.Equal(4_294_967_296L, CharacteristicRules.ComputeDistributionCost(0, 0, 0, 0, int.MaxValue, 715_827_883));
        Assert.Equal(42_949_670_540L, CharacteristicRules.ComputeDistributionCost(
            int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue));
        Assert.False(PresetHandler.Fits(new Dictionary<int, int> { [Stat.Wisdom] = 1_431_655_766 }, 200));
        Assert.False(PresetHandler.Fits(new Dictionary<int, int> { [Stat.Vitality] = int.MaxValue, [Stat.Wisdom] = 715_827_883 }, 200));
        Assert.False(PresetHandler.Fits(new Dictionary<int, int> { [Stat.Strength] = int.MaxValue }, 200));
    }

    [Theory]
    [InlineData(Stat.Strength)]
    [InlineData(Stat.Intelligence)]
    [InlineData(Stat.Chance)]
    [InlineData(Stat.Agility)]
    [InlineData(Stat.Vitality)]
    [InlineData(Stat.Wisdom)]
    public void Negative_allocations_are_rejected_by_the_cost_and_preset_boundaries(int characteristic)
    {
        int Value(int id) => characteristic == id ? -1 : 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => CharacteristicRules.ComputeDistributionCost(
            Value(Stat.Strength), Value(Stat.Intelligence), Value(Stat.Chance), Value(Stat.Agility), Value(Stat.Vitality), Value(Stat.Wisdom)));
        Assert.False(PresetHandler.Fits(new Dictionary<int, int> { [characteristic] = -1 }, 200));
    }

    [Fact]
    public void Presets_accept_the_capital_boundary_and_reject_unknown_characteristics()
    {
        Assert.True(PresetHandler.Fits(new Dictionary<int, int>(), 1));
        Assert.True(PresetHandler.Fits(new Dictionary<int, int> { [Stat.Vitality] = 995 }, 200));
        Assert.False(PresetHandler.Fits(new Dictionary<int, int> { [Stat.Vitality] = 996 }, 200));
        Assert.False(PresetHandler.Fits(new Dictionary<int, int> { [999] = 1 }, 200));
    }
}
