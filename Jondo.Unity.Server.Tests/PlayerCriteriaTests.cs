using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class PlayerCriteriaTests
{
    [Theory]
    [InlineData(true, '=', "1", Answer.True)]
    [InlineData(false, '=', "1", Answer.False)]
    [InlineData(false, '=', "0", Answer.True)]
    [InlineData(true, '!', "1", Answer.False)]
    [InlineData(false, '!', "1", Answer.True)]
    [InlineData(true, '>', "0", Answer.True)]
    [InlineData(false, '>', "0", Answer.False)]
    [InlineData(true, '<', "1", Answer.False)]
    [InlineData(false, '<', "1", Answer.True)]
    public void Ga_compares_the_post_fight_player_outcome(bool alive, char op, string value, Answer expected)
    {
        var facts = Facts(alive);

        Assert.Equal(expected, PlayerCriteria.Resolve(new Condition("Ga", op, [value]), facts));
    }

    [Theory]
    [InlineData('=', "1")]
    [InlineData('!', "1")]
    [InlineData('>', "0")]
    public void Ga_is_unknown_without_a_post_fight_outcome_even_with_session_state(char op, string value)
    {
        // Session state is not a source for the player's post-fight combatant outcome.
        var facts = Facts(null, new SessionState { IsInFight = true });

        Assert.Equal(Answer.Unknown, PlayerCriteria.Resolve(new Condition("Ga", op, [value]), facts));
    }

    [Theory]
    [InlineData(2999, '<', "3000", Answer.True)]
    [InlineData(3000, '<', "3000", Answer.False)]
    [InlineData(3000, '>', "2999", Answer.True)]
    [InlineData(0, '>', "2999", Answer.False)]
    public void PK_compares_the_kamas_the_character_carries(long kamas, char op, string value, Answer expected)
    {
        var facts = Facts(null, new SessionState { Kamas = kamas });

        Assert.Equal(expected, PlayerCriteria.Resolve(new Condition("PK", op, [value]), facts));
    }

    private static PlayerCriteria.Facts Facts(bool? playerAlive, SessionState? state = null)
        => new(state ?? new SessionState(), null, null, 0, _ => 0, PlayerAlive: playerAlive);
}
