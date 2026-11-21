using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Mutación Inestable (6759, grade 3), which Capitán Meno hands to the monsters of his fight:
    /// "-25% / -33% / -50% HP" (1048) with delays 1, 2 and 3 and a kill at delay 4. All three
    /// went off at the cast, on the maximum, and every monster died before it played. Now each
    /// waits for its round and takes its share of what is left: a quarter of the maximum a round.
    /// </summary>
    public class UnstableMutationTests
    {
        private const int MutacionInestable = 6759, Grade = 3;

        [Fact]
        public void The_life_goes_a_quarter_a_round_and_none_at_the_cast()
        {
            if (!SpellEffects.De(MutacionInestable, Grade).Any(e => e.EffectId == 1048 && e.Delay > 0)) return;   // no spell data

            var fight = new FightInstance(1, 1);
            var me = new Fighter { Id = 10, TeamId = 0, CellId = 229, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                                   MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            var crew = new Fighter { Id = -2, TeamId = 1, CellId = 315, MaxHP = 6600, CurrentHP = 6600, Level = 200,
                                     IsMonster = true, MonsterId = 4455, MaxAP = 8, CurrentAP = 8, MaxMP = 4, CurrentMP = 4 };
            fight.AddPlayer(me); fight.AddMonster(crew);
            int round = fight.RoundNumber;

            var outcomes = EffectEngine.Resolver(fight, crew, MutacionInestable, Grade, crew, EffectEngine.AlLanzar,
                                                 round, celdaApuntada: crew.CellId);
            Assert.DoesNotContain(outcomes, o => o.VidaQueSeVa > 0);
            Assert.Equal(6600, crew.CurrentHP);

            foreach (var (delay, left) in new[] { (1, 4950), (2, 3300), (3, 1650) })
            {
                foreach (var due in EffectEngine.ActivateDuePending(fight, round + delay).Where(a => a.Replays))
                {
                    var applied = EffectEngine.ResolvePending(fight, crew, due, round + delay);
                    if (applied?.VidaQueSeVa > 0) crew.CurrentHP -= applied.VidaQueSeVa;
                }
                Assert.InRange(crew.CurrentHP, left - 20, left + 20);
                Assert.True(crew.IsAlive);
            }
        }
    }
}
