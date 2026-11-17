using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// «Arrête-la si tu peux»: Karnhell climbs from Chasseur chassé I to V by starting her turns on
    /// her own glyph, and the capture net kills her at V (Dofus pour les noobs). The glyph's spell
    /// (21975) gives HER each state for one round, from the one before.
    /// </summary>
    [Collection("MapManager")]
    public class KarnhellClimbTests
    {
        private const int Karnhell = 6965;
        private const int Glyph = 21975;
        private const int StateI = 2897, StateII = 2898, StateV = 2901;

        private static (FightInstance Fight, Fighter Karn) Board()
        {
            var fight = new FightInstance(1, 1);
            var karn = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 300, MaxHP = 17000, CurrentHP = 17000, Level = 150,
                IsMonster = true, MonsterId = Karnhell, MaxAP = 8, CurrentAP = 8, MaxMP = 5, CurrentMP = 5,
            };
            var hero = new Fighter { Id = 10, TeamId = 0, CellId = 400, MaxHP = 5000, CurrentHP = 5000, Level = 200 };
            fight.AddMonster(karn); fight.AddPlayer(hero);
            return (fight, karn);
        }

        /// <summary>
        /// Her state I falls at the start of this very turn (it lasts one round), but the glyph
        /// that fires there reads it as held: she goes to II, not back to I.
        /// </summary>
        [Fact]
        public void The_state_that_falls_at_this_turn_start_still_lets_the_glyph_climb_her()
        {
            var (fight, karn) = Board();
            fight.StatesBeforeTheSweep = (karn.Id, new System.Collections.Generic.HashSet<int> { StateI });

            var outcomes = EffectEngine.Resolver(fight, karn, Glyph, 1, karn, EffectEngine.AlLanzar, fight.RoundNumber,
                                                 celdaApuntada: karn.CellId);

            Assert.Contains(outcomes, o => o.Sobre == karn && o.Buff != null && o.Buff.Estado == StateII);
            Assert.DoesNotContain(outcomes, o => o.Sobre == karn && o.Buff != null && o.Buff.Estado == StateI);
        }

        /// <summary>Without that state she starts again at I: the climb needs her turns in a row on the glyphs.</summary>
        [Fact]
        public void Without_a_state_held_the_glyph_starts_her_at_I()
        {
            var (fight, karn) = Board();

            var outcomes = EffectEngine.Resolver(fight, karn, Glyph, 1, karn, EffectEngine.AlLanzar, fight.RoundNumber,
                                                 celdaApuntada: karn.CellId);

            Assert.Contains(outcomes, o => o.Sobre == karn && o.Buff != null && o.Buff.Estado == StateI);
        }
    }
}
