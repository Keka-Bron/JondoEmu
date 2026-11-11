using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class ItemEffectMessagesTests
{
    [Fact]
    public void Internal_effects_are_omitted_and_nonempty_text_takes_precedence_over_numeric_shape()
    {
        int classified = 0;
        var messages = new ItemEffectMessages((_, _, _, _) => { classified++; return (EffectFields.Skip, 0, 0, 0); });
        Assert.Null(messages.Effect(new(-1, 123, 0, 0, "Internal")));
        Assert.Null(messages.Effect(new(0, 123, 0, 0)));
        Assert.Equal(new ObjectEffect(988, new ObjectEffectCaseValueString("Crafter")),
            messages.Effect(new(988, 999, 123, 456, "Crafter")));
        Assert.Equal(0, classified);
        Assert.Null(messages.Effect(new(988, 0, 0, 0, "")));
        Assert.Equal(1, classified);
    }

    [Theory]
    [MemberData(nameof(EmptyValues))]
    public void Action_only_selected_integer_zero_and_present_empty_compounds_keep_their_cases(int shape, ObjectEffectCase expected)
    {
        var messages = new ItemEffectMessages((_, _, _, _) => (shape, 0, 0, 0));
        Assert.Equal(new ObjectEffect(91_001, expected), messages.Effect(new(91_001, 0, 0, 0)));
    }

    public static TheoryData<int, ObjectEffectCase> EmptyValues => new()
    {
        { EffectFields.NoValue, new ObjectEffectCaseNone() },
        { EffectFields.AsNumber, new ObjectEffectCaseValueInt(0) },
        { EffectFields.AsRange, new ObjectEffectCaseValueMinMax(new(0, 0)) },
        { EffectFields.AsDice, new ObjectEffectCaseValueDice(new(0, 0, 0)) },
    };

    [Fact]
    public void Catalogue_shape_receives_the_original_three_values_and_owns_the_emitted_range()
    {
        var calls = new List<(int, long, long, long)>();
        var messages = new ItemEffectMessages((id, value, num, side) =>
        {
            calls.Add((id, value, num, side));
            return (EffectFields.AsRange, 20, 10, 0);
        });
        Assert.Equal(new ObjectEffect(91_001, new ObjectEffectCaseValueMinMax(new(20, 10))),
            messages.Effect(new(91_001, 7, 10, 20)));
        Assert.Equal(new[] { (91_001, 7L, 10L, 20L) }, calls);
    }

    [Fact]
    public void Dice_number_retains_int64_width_while_const_and_side_keep_their_values()
    {
        var messages = new ItemEffectMessages((_, _, _, _) => (EffectFields.AsDice, -12, 5_000_000_000, 28));
        Assert.Equal(new ObjectEffect(91_001, new ObjectEffectCaseValueDice(new(-12, 5_000_000_000, 28))),
            messages.Effect(new(91_001, 0, 0, 0)));
    }

    [Theory]
    [InlineData("2026-08-12T22:15:10Z", 13, 8)]
    [InlineData("2026-01-14T23:15:10Z", 15, 1)]
    public void Item_dates_use_unix_seconds_Paris_time_and_the_Dofus_year(string utc, int day, int month)
    {
        long seconds = DateTimeOffset.Parse(utc).ToUnixTimeSeconds();
        var messages = new ItemEffectMessages((_, value, _, _) => (EffectFields.AsDate, value, 0, 0));
        Assert.Equal(new ObjectEffect(805, new ObjectEffectCaseValueDate(new(day, 656, month, 15, 0))),
            messages.Effect(new(805, seconds, 0, 0)));
    }

    [Theory]
    [InlineData(EffectFields.AsNumber, 2147483648L, 0L, 0L)]
    [InlineData(EffectFields.AsNumber, -2147483649L, 0L, 0L)]
    [InlineData(EffectFields.AsRange, 0L, 2147483648L, 0L)]
    [InlineData(EffectFields.AsDice, 2147483648L, 0L, 0L)]
    [InlineData(EffectFields.AsDice, 0L, 0L, 2147483648L)]
    public void Narrow_effect_values_fail_instead_of_wrapping(int shape, long first, long second, long third)
    {
        var messages = new ItemEffectMessages((_, _, _, _) => (shape, first, second, third));
        Assert.Throws<OverflowException>(() => messages.Effect(new(91_001, 0, 0, 0)));
    }

    [Fact]
    public void Classification_failure_is_propagated_without_an_effect_fallback()
    {
        var failure = new InvalidOperationException("test catalogue failure");
        int calls = 0;
        var messages = new ItemEffectMessages((_, _, _, _) => { calls++; throw failure; });
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => messages.Effect(new(91_001, 1, 0, 0))));
        Assert.Equal(1, calls);
    }
}
