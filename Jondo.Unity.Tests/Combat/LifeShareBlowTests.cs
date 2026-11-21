using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Two blows of Missiz Frizz's forge whose number is a share of a life, not a die grown by
    /// the caster: the Verglasseur's Braquage (3512, "-75% PdV" on its enemies, 1048) and
    /// Missiz Frizz's Helada Mala (3524, "35% of the target's eroded HP" as air, 1093).
    /// </summary>
    [Collection("MapManager")]
    public class LifeShareBlowTests
    {
        private const int Verglasseur = 3389, Braquage = 3512, HeladaMalaBlow = 3524, Invulnerable = 56;

        private static (FightInstance, Fighter, Fighter, Fighter) Forge()
        {
            var fight = new FightInstance(1, 1);
            var shielded = new Fighter
            {
                Id = 10, TeamId = 0, CellId = 300, MaxHP = 20000, CurrentHP = 20000, Level = 200,
                MaxAP = 12, CurrentAP = 12,
            };
            var bare = new Fighter
            {
                Id = 11, TeamId = 0, CellId = 301, MaxHP = 20000, CurrentHP = 20000, Level = 200,
                MaxAP = 12, CurrentAP = 12,
            };
            var monster = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 475, MaxHP = 6000, CurrentHP = 6000, Level = 200,
                IsMonster = true, MonsterId = Verglasseur, Agility = 700,
            };
            fight.AddPlayer(shielded); fight.AddPlayer(bare); fight.AddMonster(monster);
            return (fight, shielded, bare, monster);
        }

        [Fact]
        public async Task Braquage_takes_its_share_of_life_from_all_but_the_invulnerable()
        {
            if (SpellEffects.De(Braquage, 1).Count == 0 || SpellStates.Of(Invulnerable) == null) return;
            var (fight, shielded, bare, monster) = Forge();
            shielded.Buffs.PonerEstado(Invulnerable);

            await FightHandler.AplicarEfectosAsync(null, fight, monster, Braquage, 1, shielded,
                                                   EffectEngine.AlLanzar, shielded.CellId);

            Assert.Equal(20000, shielded.CurrentHP);
            Assert.Equal(5000, bare.CurrentHP);
        }

        [Fact]
        public async Task The_eroded_share_is_not_grown_by_the_caster_s_characteristics()
        {
            var (fight, _, bare, monster) = Forge();
            Assert.Equal(1000, bare.Erosionar(10000, 10));
            int before = bare.CurrentHP;

            // Helada Mala's row: "Daños de aire: 35% PdV erosionados del objetivo".
            var row = new SpellEffect { EffectId = 1093, EffectUid = 900401, DiceNum = 35, TargetMask = "A" };
            await FightHandler.HurtAsync(null, fight, monster, HeladaMalaBlow, 1, bare, bare.CellId,
                                         tirada: new[] { row });

            // 35% of the 1,000 eroded; grown by the 700 agility it was eight times that.
            Assert.Equal(350, before - bare.CurrentHP);
        }
    }
}
