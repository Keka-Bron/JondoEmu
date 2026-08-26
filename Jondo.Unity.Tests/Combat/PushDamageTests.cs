using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The collision damage formula, against the captures that measured it.
    /// </summary>
    /// <remarks>
    /// Ankama publishes this nowhere: neither the client's constant bundle nor its 38 Lua formulas
    /// carry anything about combat, so the whole thing comes from measuring 127 collisions across
    /// the 401 captures.
    ///
    ///     damage = blockedCells × (level/2 + pusher's 84 − target's 85 + 32) / 4
    ///
    /// Each anchor below pins a different term, and none of them is explained by coincidence. If
    /// somebody moves the 32, pulls the resistance out of the parenthesis, or rounds per tile
    /// instead of at the end, one of these falls over.
    /// </remarks>
    public class PushDamageTests
    {
        /// <summary>
        /// A level-200 pusher with no bonus deals 33 per tile — 132 over four — and the captures
        /// only ever show 33, 66, 99 and 132. Not one value in between.
        /// </summary>
        [Theory]
        [InlineData(1, 33)]
        [InlineData(2, 66)]
        [InlineData(3, 99)]
        [InlineData(4, 132)]
        public void A_level_200_pusher_with_no_bonus_deals_33_per_tile(int cells, int expected)
        {
            Assert.Equal(expected, EffectEngine.DanoDeColision(200, 0, 0, cells));
        }

        /// <summary>
        /// The Zurkarak "Daddy" is level 165 and deals 57 over two tiles, which is
        /// floor(2 × 114.5 / 4). No fixed constant can produce that number — a flat 132 would give
        /// 66 — so this is what anchors the level term, and it is the sample that closed the
        /// formula after the first pass had given it up as unknowable.
        /// </summary>
        [Fact]
        public void The_level_term_is_real_and_a_level_165_pusher_proves_it()
        {
            Assert.Equal(57, EffectEngine.DanoDeColision(165, 0, 0, 2));
            Assert.NotEqual(66, EffectEngine.DanoDeColision(165, 0, 0, 2));
        }

        /// <summary>
        /// A Zobal carrying 100 of push damage from gear, with masks adding 0, 40, 80 and 120. All
        /// four exact, which is what anchors characteristic 84.
        /// </summary>
        [Theory]
        [InlineData(100, 58)]
        [InlineData(140, 68)]
        [InlineData(180, 78)]
        [InlineData(220, 88)]
        public void The_pushers_own_push_damage_adds(int push, int expected)
        {
            Assert.Equal(expected, EffectEngine.DanoDeColision(200, push, 0, 1));
        }

        /// <summary>
        /// From a Koliseo capture: 561 of push damage against 30 of resistance gives 331 over two
        /// tiles. Subtracting the resistance outside the quarter would give 316, so this is what
        /// proves it belongs inside.
        /// </summary>
        [Fact]
        public void The_resistance_is_subtracted_inside_the_quarter()
        {
            Assert.Equal(331, EffectEngine.DanoDeColision(200, 561, 30, 2));
            Assert.Equal(389, EffectEngine.DanoDeColision(200, 561, 174, 3));
        }

        // ─── Boundaries ───────────────────────────────────────────────────────────

        [Fact]
        public void No_blocked_tiles_is_no_damage()
        {
            // Travelling the whole push is the common case and it must cost nothing: the server
            // does not even send the message then.
            Assert.Equal(0, EffectEngine.DanoDeColision(200, 0, 0, 0));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        public void A_negative_tile_count_is_no_damage_rather_than_healing(int cells)
        {
            Assert.Equal(0, EffectEngine.DanoDeColision(200, 0, 0, cells));
        }

        [Fact]
        public void An_enormous_resistance_floors_at_zero_and_never_gives_life_back()
        {
            // Across the 401 captures there is not a single collision message carrying zero
            // damage, so the server does not announce this case; it must not announce a negative
            // one either.
            Assert.Equal(0, EffectEngine.DanoDeColision(1, 0, 500, 3));
        }

        [Fact]
        public void A_level_one_pusher_still_hurts_a_little()
        {
            // level/2 = 0, so the 32 carries it alone: floor(1 × 32 / 4).
            Assert.Equal(8, EffectEngine.DanoDeColision(1, 0, 0, 1));
        }

        [Fact]
        public void The_arithmetic_does_not_overflow_on_absurd_input()
        {
            // Nothing in the game reaches this, but the value arrives from a characteristic that a
            // buff can move, and an overflow here would come out as damage that heals.
            int dealt = EffectEngine.DanoDeColision(200, int.MaxValue / 8, 0, 4);
            Assert.True(dealt > 0);
        }
    }
}
