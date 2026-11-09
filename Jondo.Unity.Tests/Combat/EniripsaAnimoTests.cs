using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Eniripsa's ánimos land through a child that first takes the other ánimos off: Palabra
    /// Estimulante's 25761 casts its grade 3 (+2 AP, Animado) on "a,p" -- the characters and the
    /// others' summons -- and on "a,P,f7371,f7370" -- her own summons but the fairies. A
    /// character is not hers, so only the first is his: the bonus once. Read as "P lets anybody
    /// who is no summon through", the character met both and took +4 AP.
    /// </summary>
    public class EniripsaAnimoTests
    {
        private const int PalabraEstimulante = 25742;

        [Fact]
        public void An_animo_on_a_character_gives_its_bonus_once()
        {
            var fight = new FightInstance(1, 1);
            var eniripsa = new Fighter { Id = 10, TeamId = 0, CellId = 300, MaxHP = 1000, CurrentHP = 1000, MaxAP = 12, CurrentAP = 12, Breed = 7, Level = 200 };
            var ally = new Fighter { Id = 11, TeamId = 0, CellId = 302, MaxHP = 1000, CurrentHP = 1000, MaxAP = 12, CurrentAP = 12, Breed = 8, Level = 200 };
            fight.AddPlayer(eniripsa);
            fight.AddPlayer(ally);
            fight.AddMonster(new Fighter { Id = -50, TeamId = 1, CellId = 200, MaxHP = 500, CurrentHP = 500 });

            EffectEngine.Resolver(fight, eniripsa, PalabraEstimulante, 3, ally, EffectEngine.AlLanzar, fight.RoundNumber, celdaApuntada: ally.CellId);

            var points = ally.Buffs.Puestos.Where(r => r.EffectId == 111).ToList();
            Assert.Single(points);
            Assert.Equal(2, ally.Buffs.De(1, fight.RoundNumber));
        }
    }
}
