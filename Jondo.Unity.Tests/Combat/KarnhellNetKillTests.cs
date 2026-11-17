using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// «Arrête-la si tu peux»: the net's «¡Puedes atraparme!» (21988 grade 3) is cast BY KARNHELL
    /// (her own 21974 hooks it on the state the net gives her), and it kills the enemies of the
    /// caster when she is not at Chasseur chassé V ("Ne lancez pas le sort avant votre tour 5
    /// sinon vous serez OS") and the caster herself when she is. Its two rows carry the conditions
    /// on the CASTER (the starred masks "A,*e2901" and "C,*E2901"). Cast by the player, who is
    /// never at V, the same spell would kill her: that was the retest's bug 01, a spell of hers
    /// cast through the test harness and not the net's own.
    /// </summary>
    [Collection("MapManager")]
    public class KarnhellNetKillTests
    {
        private const int Karnhell = 6965, Catch = 21988, GradeThree = 3, StateV = 2901;

        private static (FightInstance Fight, Fighter Karn, Fighter Hero) Board()
        {
            var fight = new FightInstance(1, 1);
            var karn = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 300, MaxHP = 17000, CurrentHP = 17000, Level = 150,
                IsMonster = true, MonsterId = Karnhell, MaxAP = 8, CurrentAP = 8, MaxMP = 5, CurrentMP = 5,
            };
            var hero = new Fighter { Id = 10, TeamId = 0, CellId = 400, MaxHP = 5000, CurrentHP = 5000, Level = 200 };
            fight.AddMonster(karn); fight.AddPlayer(hero);
            return (fight, karn, hero);
        }

        private static System.Collections.Generic.List<Outcome> CastByKarnhell(FightInstance fight, Fighter karn)
            => EffectEngine.Resolver(fight, karn, Catch, GradeThree, karn, EffectEngine.AlLanzar, fight.RoundNumber,
                                     celdaApuntada: karn.CellId);

        [Fact]
        public void Before_the_state_V_the_net_kills_the_one_who_cast_it_on_her_and_not_her()
        {
            var (fight, karn, hero) = Board();

            var outcomes = CastByKarnhell(fight, karn);

            Assert.Contains(outcomes, o => o.Efecto.EffectId == EffectEngine.MataAlObjetivo && o.Sobre == hero);
            Assert.DoesNotContain(outcomes, o => o.Efecto.EffectId == EffectEngine.MataAlObjetivo && o.Sobre == karn);
        }

        [Fact]
        public void At_the_state_V_the_net_kills_her_and_spares_the_player()
        {
            var (fight, karn, hero) = Board();
            karn.Buffs.PonerEstado(StateV);

            var outcomes = CastByKarnhell(fight, karn);

            Assert.Contains(outcomes, o => o.Efecto.EffectId == EffectEngine.MataAlObjetivo && o.Sobre == karn);
            Assert.DoesNotContain(outcomes, o => o.Efecto.EffectId == EffectEngine.MataAlObjetivo && o.Sobre == hero);
        }
    }
}
