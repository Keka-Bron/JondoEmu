using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The V/v criteria of a target mask: life under, or not under, a percentage of the maximum.
    /// </summary>
    /// <remarks>
    /// 1,100 uses across the catalogue, and until now every one of them was silently ignored --
    /// an ignored condition being a condition met. The Silver Dofus, "C,V20", healed its bearer
    /// to full at the start of every turn whatever his life was.
    ///
    /// The letter follows the case rule of F/f and E/e, measured on Ataque Mortal ("danos
    /// mayores en objetivos con menos del 50%"): its big die carries V50, its small one v50.
    /// </remarks>
    public class LifeThresholdMaskTests
    {
        /// <summary>
        /// Promesa de Plata, grade 2: under "C,V20", sub-casts the grade that sets state 2155 and
        /// disarms itself. "Cuando el portador tiene menos de un 20% de vida".
        /// </summary>
        private const int PromesaDePlata = 18672;
        private const int Centelleante = 2155;

        private static (FightInstance Fight, Fighter Bearer) Arena(int life)
        {
            var fight = new FightInstance(1, 1);
            var bearer = new Fighter { Id = 10, TeamId = 0, CellId = 300, MaxHP = 1000, CurrentHP = life };
            bearer.Buffs.Actitudes.Add(PromesaDePlata);
            fight.AddPlayer(bearer);
            return (fight, bearer);
        }

        [Fact]
        public void Under_is_strict_and_reads_the_current_maximum()
        {
            var who = new Fighter { MaxHP = 945, CurrentHP = 189 };
            Assert.False(EffectEngine.LifeUnder(who, 20));   // exactly 20% is not under
            who.CurrentHP = 188;
            Assert.True(EffectEngine.LifeUnder(who, 20));
        }

        [Fact]
        public void The_Silver_Dofus_does_nothing_above_a_fifth_of_life()
        {
            var (fight, bearer) = Arena(life: 250);

            EffectEngine.Resolver(fight, bearer, PromesaDePlata, 2, bearer,
                                  EffectEngine.AlLanzar, fight.RoundNumber,
                                  celdaApuntada: bearer.CellId);

            Assert.DoesNotContain(Centelleante, bearer.Buffs.Estados);
            Assert.Contains(PromesaDePlata, bearer.Buffs.Actitudes);
        }

        [Fact]
        public void The_Silver_Dofus_arms_itself_under_a_fifth_and_only_once()
        {
            var (fight, bearer) = Arena(life: 150);

            EffectEngine.Resolver(fight, bearer, PromesaDePlata, 2, bearer,
                                  EffectEngine.AlLanzar, fight.RoundNumber,
                                  celdaApuntada: bearer.CellId);

            Assert.Contains(Centelleante, bearer.Buffs.Estados);

            // "1 vez por combate": the grade carries a 406 on 18672 itself, which takes the
            // passive away. Buffs alone used to go and the attitude stayed armed.
            Assert.DoesNotContain(PromesaDePlata, bearer.Buffs.Actitudes);
        }
    }
}
