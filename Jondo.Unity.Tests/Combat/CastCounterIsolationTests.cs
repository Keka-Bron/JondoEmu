using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The per-turn cast counters, which used to be one set for the whole process.
    /// </summary>
    /// <remarks>
    /// Two dictionaries lived in a static field of <c>FightHandler</c>, keyed only by the spell id.
    /// That is the bug class this project already went through once with session state, and it fails
    /// the same way: with two clients in two fights, one player's casts counted against the other's
    /// as soon as they shared a spell — and spell ids repeat across players.
    ///
    /// The half that made it worse than a mix-up: nothing ever emptied them. The only call to the
    /// method that cleared them sat inside a handler with no caller of its own, so after three casts
    /// of a spell — summed over every player and every fight since the server started — that spell
    /// was refused with "already spent this turn" for everybody until a restart.
    /// </remarks>
    public class CastCounterIsolationTests
    {
        [Fact]
        public void Two_fights_do_not_share_a_cast_count()
        {
            var one = new FightInstance(1, 100, 0);
            var other = new FightInstance(2, 100, 0);

            one.CastsThisTurn[(caster: 10L, spell: 161L)] = 3;

            Assert.False(other.CastsThisTurn.ContainsKey((10L, 161L)));
            Assert.Empty(other.CastsThisTurn);
        }

        [Fact]
        public void Two_casters_in_the_same_fight_do_not_share_one_either()
        {
            // The key carries the caster, so the same spell cast by two players in the same fight is
            // two counts. Keyed by spell alone — as it was — the second player would arrive with the
            // first one's total already spent.
            var fight = new FightInstance(1, 100, 0);

            fight.CastsThisTurn[(10L, 161L)] = 2;
            fight.CastsThisTurn[(11L, 161L)] = 0;

            Assert.Equal(2, fight.CastsThisTurn[(10L, 161L)]);
            Assert.Equal(0, fight.CastsThisTurn[(11L, 161L)]);
        }

        [Fact]
        public void Moving_to_the_next_turn_empties_them()
        {
            // Where the clearing has to happen, and where it did not. NextTurn is the one place the
            // turn actually changes; the old reset was in a method nothing called.
            var fight = new FightInstance(1, 100, 0);
            fight.CastsThisTurn[(10L, 161L)] = 3;
            fight.CastsPerTargetThisTurn[(10L, 161L, 20L)] = 1;

            fight.NextTurn();

            Assert.Empty(fight.CastsThisTurn);
            Assert.Empty(fight.CastsPerTargetThisTurn);
        }
    }
}
