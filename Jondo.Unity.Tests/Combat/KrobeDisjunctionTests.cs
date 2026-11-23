using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Krobes are invulnerable (4942: state 56), and a blow on one casts its Disjunction (4935:
    /// 1123 on 'j,*E214', 200% of the initial damage suffered) from a Krobe in state 214 at the
    /// Malamibe: the one way to hurt her. With the blow worked out as 0 before the shield, she was
    /// never touched.
    /// </summary>
    [Collection("MapManager")]
    public class KrobeDisjunctionTests
    {
        private const int KrobeShield = 4942;
        private const int Invulnerable = 56;
        private const int LinkedToTheMalamibe = 214;

        [Fact]
        public async Task A_blow_on_an_invulnerable_Krobe_hurts_the_Malamibe()
        {
            if (!SpellEffects.De(KrobeShield, 1).Any(row => row.EffectId == 792)) return;   // no spell data
            var player = new Fighter
            {
                Id = 9_970_701, Name = "P", CellId = 250, Level = 200, MaxHP = 5000, CurrentHP = 5000,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
            };
            var krobe = new Fighter
            {
                Id = -1, Name = "Krobe", IsMonster = true, Level = 200, MaxHP = 4000, CurrentHP = 4000,
                MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4, MonsterId = 3824,
            };
            var malamibe = new Fighter
            {
                Id = -2, Name = "Malamibe", IsMonster = true, Level = 200, MaxHP = 9000, CurrentHP = 9000,
                MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4, MonsterId = 3822,
            };
            var fight = new FightInstance(3824, 990_003_824);
            fight.AddPlayer(player);
            fight.AddMonster(krobe);
            fight.AddMonster(malamibe);
            player.CellId = 250; krobe.CellId = 251; malamibe.CellId = 300;
            malamibe.Invocador = krobe.Id;
            fight.StartFight();
            krobe.Buffs.PonerEstado(LinkedToTheMalamibe);   // 4933, her behaviour, on all her allies

            await FightHandler.AplicarEfectosAsync(null, fight, krobe, KrobeShield, 1, krobe,
                                                   EffectEngine.AlLanzar, krobe.CellId);
            Assert.True(krobe.Buffs.TieneEstado(Invulnerable));

            var blow = SpellEffects.De(6773, 1).First(e => e.EffectId == 97);
            await FightHandler.UnGolpeAsync(null, fight, player, 6773, blow, 4, krobe, 95, 0, false);
            Assert.Equal(4000, krobe.CurrentHP);
            Assert.True(malamibe.CurrentHP < 9000);
        }
    }
}
