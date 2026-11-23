using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Sombra's Pyramid (Dofus pour les noobs, «Pyramide d'Ombre»): Sombra (3564) summons one
    /// Globiluz (3563), whose turn end lays a C3 aura glyph (8830). Sombra is open while his
    /// Silueta (3565) stands in it and he stands out of it, and closed again when that ends.
    /// Sombra's behaviour (4104) writes the Globiluz twice, on X9 and on X5-10, with no draw: the
    /// second is the first's fallback. Both were born, and the C3 light thrown round the Silueta
    /// lit Sombra too and was never taken back, so he piled up five lights and stayed closed.
    /// </summary>
    [Collection("MapManager")]
    public class SombraGlyphTests
    {
        private const int Sombra = 3564, Globiluz = 3563, Silueta = 3565;
        private const int Invulnerable = 56, Light = 536;

        private static Fighter Monster(long id, int monsterId, int cell, (int, int) behaviour) => new()
        {
            Id = id, Name = "M", IsMonster = true, Level = 200, MaxHP = 20000, CurrentHP = 20000, TeamId = 1,
            MaxAP = 12, CurrentAP = 12, MaxMP = 4, CurrentMP = 4, MonsterId = monsterId, CellId = cell,
            Conducta = behaviour,
        };

        [Fact]
        public async Task One_globiluz_and_its_light_opens_sombra_while_the_silueta_stands_in_it()
        {
            if (!SpellEffects.De(4104, 1).Any(row => row.EffectId == 181)) return;   // no spell data
            MobSpawnManager.EnsureMonsterData();
            var player = new Fighter
            {
                Id = 9_974_001, Name = "P", CellId = 100, Level = 200, MaxHP = 5000, CurrentHP = 5000,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
            };
            var sombra = Monster(-1, Sombra, 300, (4104, 1));
            var silueta = Monster(-2, Silueta, 330, (4117, 1));
            var fight = new FightInstance(3564, 990_003_564);
            fight.AddPlayer(player);
            fight.AddMonster(sombra);
            fight.AddMonster(silueta);
            player.CellId = 100; sombra.CellId = 300; silueta.CellId = 330;
            fight.StartFight();

            await FightHandler.LanzarLaConductaAsync(null, fight, sombra);
            await FightHandler.LanzarLaConductaAsync(null, fight, silueta);
            var globiluz = Assert.Single(fight.Todos, f => f.IsAlive && f.MonsterId == Globiluz);
            Assert.True(sombra.Buffs.TieneEstado(Invulnerable));

            // His turn end lays the light round him.
            while (fight.CurrentFighter != globiluz) fight.NextTurn();
            await FightHandler.EngancheAsync(null, fight, globiluz, EffectEngine.AlAcabarElTurno);
            var glyph = Assert.Single(fight.Glifos, g => g.Dueno == globiluz.Id);

            // The Silueta walks in; Sombra stands out of it, but within the light's C3 of her.
            int inside = Enumerable.Range(0, 560).First(c => glyph.Cubre(c) && c != globiluz.CellId
                                                             && c != sombra.CellId && c != player.CellId);
            int outside = Enumerable.Range(0, 560).First(c => !glyph.Cubre(c) && MapGeometry.Distance(inside, c) <= 2
                                                              && c != player.CellId);
            sombra.CellId = outside;
            silueta.CellId = inside;
            await FightHandler.DispararLosGlifosAsync(null, fight, sombra, alPisar: true);
            await FightHandler.DispararLosGlifosAsync(null, fight, silueta, alPisar: true);
            Assert.True(silueta.Buffs.TieneEstado(Light));
            Assert.False(sombra.Buffs.TieneEstado(Light));
            Assert.False(sombra.Buffs.TieneEstado(Invulnerable));

            // The light lasts a round: at his next turn start it drops, and with it what it gave.
            int round = fight.RoundNumber;
            while (fight.CurrentFighter != globiluz || fight.RoundNumber == round) fight.NextTurn();
            await FightHandler.QuitarLosGlifosCaducadosAsync(fight, globiluz);
            Assert.DoesNotContain(fight.Glifos, g => g.Id == glyph.Id);
            Assert.False(silueta.Buffs.TieneEstado(Light));
            Assert.True(sombra.Buffs.TieneEstado(Invulnerable));
        }
    }
}
