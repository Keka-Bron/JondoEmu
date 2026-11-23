using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Korriandre (2968) is invulnerable (state 56 from his behaviour, Ket 2569). A blow on him
    /// makes the attacker lay 2699: a turn-start glyph (2700) and a turn-end glyph (2701). A
    /// Sporakne (2969) ending its turn on the turn-end glyph takes state 279 and dies, and Ket
    /// waits on that death (EK:i,F2969,E279) to lift state 56.
    /// </summary>
    [Collection("MapManager")]
    public class KorriandreGlyphTests
    {
        private const int Korriandre = 2968, Sporakne = 2969, Ket = 2569, Glyphs = 2699;

        [Fact]
        public async Task A_sporakne_ending_its_turn_on_the_glyph_lifts_his_invulnerability()
        {
            if (!SpellEffects.De(Ket, 1).Any(row => row.EffectId == 793)) return;   // no spell data
            var player = new Fighter
            {
                Id = 9_970_968, Name = "P", CellId = 400, Level = 200, MaxHP = 5000, CurrentHP = 5000,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
            };
            var boss = new Fighter
            {
                Id = -1, Name = "K", IsMonster = true, CellId = 300, Level = 180, MaxHP = 9000,
                CurrentHP = 9000, TeamId = 1, MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4,
                MonsterId = Korriandre,
            };
            var spore = new Fighter
            {
                Id = -2, Name = "S", IsMonster = true, CellId = 314, Level = 180, MaxHP = 600,
                CurrentHP = 600, TeamId = 1, MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3,
                MonsterId = Sporakne, Invocador = boss.Id,
            };
            var fight = new FightInstance(Korriandre, 990_002_968);
            fight.AddPlayer(player);
            fight.AddMonster(boss);
            fight.AddMonster(spore);
            player.CellId = 400; boss.CellId = 300; spore.CellId = 314;
            fight.StartFight();

            boss.Conducta = (Ket, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, boss);
            Assert.True(boss.Buffs.TieneEstado(56));

            // The glyphs a blow on him makes the attacker lay, under the Sporakne.
            await FightHandler.AplicarEfectosAsync(null, fight, player, Glyphs, 1, null,
                                                   EffectEngine.AlLanzar, spore.CellId);
            Assert.Contains(fight.Glifos, g => g.Hechizo == 2701);

            await FightHandler.TurnEndGlyphsAsync(null, fight, spore);
            Assert.False(spore.IsAlive);
            Assert.False(boss.Buffs.TieneEstado(56));
        }
    }
}
