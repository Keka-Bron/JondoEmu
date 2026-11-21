using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Nileza's Cohobation (3545) answers a ranged blow with two casts: the reflection (3587) and
    /// the swap with the attacker (3566). «Le renvoi de dégâts a toujours lieu après l'échange de
    /// place du Nileza»: the swap goes first, so the circle is drawn around her new cell.
    /// </summary>
    public class NilezaTests
    {
        private const int Cohobation = 3545;

        [Fact]
        public void The_swap_goes_ahead_of_the_reflection()
        {
            var rows = SpellEffects.De(Cohobation, 1);
            if (!rows.Any(row => row.DiceNum == 3566) || !rows.Any(row => row.DiceNum == 3587)) return;

            var first = EffectEngine.SwapsBeforeTheRest(Cohobation, 1, EffectEngine.CuandoMePeganDeLejos);

            Assert.Single(first);
            Assert.Equal(3566, first[0].DiceNum);
        }

        [Fact]
        public void A_hook_armed_with_both_casts_sends_the_swap_first()
        {
            var fired = SpellEffects.De(Cohobation, 1)
                                    .Where(row => row.Disparadores().Contains(EffectEngine.CuandoMePeganDeLejos))
                                    .ToList();
            if (fired.Count < 2) return;

            var first = EffectEngine.SwapsBeforeTheRest(fired);

            Assert.Single(first);
            Assert.Equal(3566, first[0].DiceNum);
        }

        [Fact]
        public void Nothing_is_split_for_a_trigger_that_casts_no_swap()
        {
            if (SpellEffects.De(Cohobation, 1).Count == 0) return;
            Assert.Empty(EffectEngine.SwapsBeforeTheRest(Cohobation, 1, EffectEngine.AlEmpezarElTurno));
        }
    }
}
