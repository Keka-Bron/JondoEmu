using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Protozorreur's Infection: state 215 sets off 4971 on him (its delayed 141 is the
    /// count to his death), and 4971's turn-end 4970 ("c,*e215") lifts 4971 again at the first
    /// turn's end he ends out of the state.
    /// </summary>
    [Collection("MapManager")]
    public class ProtozorreurInfectionTests
    {
        [Fact]
        public async Task The_count_goes_at_the_first_turn_end_out_of_Infection()
        {
            if (!SpellEffects.De(4971, 1).Any(row => row.EffectId == 141)) return;   // no spell data
            var fight = new FightInstance(1, 1);
            var boss = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 215, MaxHP = 20000, CurrentHP = 20000, Level = 200,
                IsMonster = true, MonsterId = 3828, MaxAP = 12, CurrentAP = 12, MaxMP = 4, CurrentMP = 4,
            };
            var player = new Fighter
            {
                Id = 10, TeamId = 0, CellId = 300, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            fight.AddMonster(boss); fight.AddPlayer(player);
            boss.CellId = 215; player.CellId = 300;
            boss.Buffs.PonerActitud(4972);
            fight.StartFight();

            // The glyph's 950 215 on 'c': the boss stepping on his own Infection.
            await FightHandler.AplicarEfectosAsync(null, fight, boss, 4966, 1, boss,
                                                   EffectEngine.AlLanzar, boss.CellId);
            Assert.True(boss.Buffs.TieneEstado(215));
            Assert.True(boss.Buffs.LlevaDelHechizo(4971));

            // Still infected at his turn's end: the count stays.
            await FightHandler.DispararAsync(null, fight, boss, EffectEngine.AlAcabarElTurno);
            Assert.True(boss.Buffs.LlevaDelHechizo(4971));

            boss.Buffs.QuitarEstadoConEmbrujos(215);
            Assert.False(boss.Buffs.TieneEstado(215));
            await FightHandler.DispararAsync(null, fight, boss, EffectEngine.AlAcabarElTurno);
            Assert.False(boss.Buffs.Puestos.Any(b => b.HechizoOrigen == 4971 && b.EffectId == 141));
        }
    }
}
