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
    /// The anomaly guardians' fights, which come from their starting spells' rows.
    /// </summary>
    [Collection("MapManager")]
    public class AnomalyGuardianTests
    {
        private static Fighter Character(long id, int cell) => new()
        {
            Id = id, Name = "P" + id, CellId = cell, Level = 200, MaxHP = 3000, CurrentHP = 3000,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
        };

        private static Fighter Monster(long id, int cell) => new()
        {
            Id = id, Name = "M" + id, IsMonster = true, CellId = cell, Level = 50, MaxHP = 900, CurrentHP = 900,
            MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, MonsterId = 494,
        };

        /// <summary>
        /// A Proto-Noxine answers a blow with one row under "D|DCAC" (12420's 1017 of 12421):
        /// a melee blow sets off both triggers, and the row still goes off once -- the attacker
        /// loses 2 % of his vitality once, not twice.
        /// </summary>
        [Fact]
        public async Task A_melee_blow_on_a_Proto_Noxine_takes_its_toll_once()
        {
            var attacker = Character(53000000031, 300);
            var noxine = Monster(-1, 300);
            noxine.CellId = Enumerable.Range(0, 560).First(c => MapGeometry.Distance(300, c) == 1);
            int noxineCell = noxine.CellId;
            var fight = new FightInstance(1, 1);
            fight.AddPlayer(attacker);
            fight.AddMonster(noxine);
            attacker.CellId = 300;
            noxine.CellId = noxineCell;
            fight.StartFight();
            noxine.Buffs.PonerActitud(12420);

            var blow = SpellEffects.De(6773, 1).First(e => e.EffectId == 97);
            await FightHandler.UnGolpeAsync(null, fight, attacker, 6773, blow, 4, noxine, 95, 0, false);

            Assert.True(noxine.CurrentHP < noxine.MaxHP);
            Assert.Single(attacker.Buffs.Puestos.Where(b => b.EffectId == 2845));
        }

        /// <summary>
        /// Leorictus's impatience climbs 1054, 1055, 1056 and then 1074 (12163 grade 1, read in
        /// one go off the states he started it with); 1074 is Impatience IV, and his starting
        /// spell answers its EON with 12185 grade 1, which takes his invulnerability off.
        /// </summary>
        [Fact]
        public async Task Leorictus_loses_his_invulnerability_at_Impatience_IV()
        {
            var player = Character(53000000032, 300);
            var leorictus = Monster(-1, 330);
            leorictus.MonsterId = 5688;
            var fight = new FightInstance(1, 1);
            fight.AddPlayer(player);
            fight.AddMonster(leorictus);
            player.CellId = 300;
            leorictus.CellId = 330;
            fight.StartFight();
            leorictus.Buffs.PonerActitud(12162);
            leorictus.Buffs.PonerEstado(56);
            leorictus.Buffs.PonerEstado(1056);

            await FightHandler.AplicarEfectosAsync(null, fight, leorictus, 12163, 1, leorictus,
                                                   EffectEngine.AlLanzar, leorictus.CellId);

            Assert.True(leorictus.Buffs.TieneEstado(1074));
            Assert.False(leorictus.Buffs.TieneEstado(1056));
            Assert.False(leorictus.Buffs.TieneEstado(56));
        }

        /// <summary>
        /// Asleep, Agony is invulnerable, and a blow on her in melee still answers with 12347
        /// grade 2 (12105's "DM" row): her Radiation moves on to its first stage.
        /// </summary>
        [Fact]
        public async Task A_melee_blow_on_a_sleeping_Agony_moves_her_radiation_on()
        {
            var attacker = Character(53000000034, 300);
            var agony = Monster(-1, 300);
            agony.MonsterId = 5684;
            agony.CellId = Enumerable.Range(0, 560).First(c => MapGeometry.Distance(300, c) == 1);
            int agonyCell = agony.CellId;
            var fight = new FightInstance(1, 1);
            fight.AddPlayer(attacker);
            fight.AddMonster(agony);
            attacker.CellId = 300;
            agony.CellId = agonyCell;
            fight.StartFight();
            agony.Buffs.PonerActitud(12105);
            agony.Buffs.PonerEstado(56);

            var blow = SpellEffects.De(6773, 1).First(e => e.EffectId == 97);
            await FightHandler.UnGolpeAsync(null, fight, attacker, 6773, blow, 4, agony, 95, 0, false);

            Assert.Equal(agony.MaxHP, agony.CurrentHP);
            Assert.True(agony.Buffs.TieneEstado(1038));
        }

        /// <summary>
        /// The Champion of the Crimson Dawn (11957) makes his chronomorphs invulnerable and lays
        /// 11959 on everyone: a vulnerable chronomorph hit with earth takes the invulnerability off
        /// those in line with it, and hit with air off those in diagonal with it, never the
        /// Champion's; it gets 967, and with it its invulnerability back at its next turn.
        /// </summary>
        [Fact]
        public async Task The_Champions_chronomorphs_lose_their_invulnerability_in_line_or_in_diagonal()
        {
            var (x, y) = MapGeometry.CellToPoint(300);
            int inLine = MapGeometry.PointToCell(x + 3, y);
            int inDiagonal = MapGeometry.PointToCell(x - 2, y - 2);
            int attackerCell = MapGeometry.PointToCell(x, y + 1);
            int championCell = MapGeometry.PointToCell(x + 5, y);

            var attacker = Character(53000000035, attackerCell);
            var champion = Monster(-1, championCell);
            champion.MonsterId = 5662;
            var hit = Monster(-2, 300);
            hit.MonsterId = 5710;
            var lined = Monster(-3, inLine);
            lined.MonsterId = 5711;
            var diagonal = Monster(-4, inDiagonal);
            diagonal.MonsterId = 5713;
            var fight = new FightInstance(1, 1);
            fight.AddPlayer(attacker);
            foreach (var m in new[] { champion, hit, lined, diagonal }) fight.AddMonster(m);
            attacker.CellId = attackerCell;
            champion.CellId = championCell;
            hit.CellId = 300;
            lined.CellId = inLine;
            diagonal.CellId = inDiagonal;
            fight.StartFight();

            await FightHandler.AplicarEfectosAsync(null, fight, champion, 11957, 1, champion,
                                                   EffectEngine.AlLanzar, champion.CellId);
            Assert.All(new[] { champion, hit, lined, diagonal }, m => Assert.True(m.Buffs.TieneEstado(56)));
            hit.Buffs.QuitarEstadoConEmbrujos(56);
            hit.Buffs.QuitarEstado(56);

            var earth = SpellEffects.De(6773, 1).First(e => e.EffectId == 97);
            await FightHandler.UnGolpeAsync(null, fight, attacker, 6773, earth, 1, hit, 20, 0, false);

            Assert.True(hit.Buffs.TieneEstado(967));
            Assert.False(lined.Buffs.TieneEstado(56));
            Assert.True(diagonal.Buffs.TieneEstado(56));
            Assert.True(champion.Buffs.TieneEstado(56));

            var air = earth.ComoEfecto(98);
            await FightHandler.UnGolpeAsync(null, fight, attacker, 6773, air, 4, hit, 20, 0, false);
            Assert.False(diagonal.Buffs.TieneEstado(56));
        }

        /// <summary>
        /// Julith's starting spell (11966) hands each of her four chronomorphs a token at random
        /// (11971, 25 % each); one that draws a token another already holds hands that one the
        /// next, and 11971's maxStack of one takes the old token off it. The tokens then become
        /// the Slayer states 956-959 (11972): one each, four kinds, whatever the draw.
        /// </summary>
        [Fact]
        public async Task Julith_gives_each_chronomorph_one_Slayer_state_of_its_own()
        {
            int[] slayer = { 956, 957, 958, 959 };
            for (int draw = 0; draw < 25; draw++)
            {
                var player = Character(53000000036, 300);
                var julith = Monster(-1, 330);
                julith.MonsterId = 5663;
                var chronomorphs = Enumerable.Range(2, 4).Select(i => Monster(-i, 330 + 2 * i)).ToList();
                var fight = new FightInstance(1, 1);
                fight.AddPlayer(player);
                fight.AddMonster(julith);
                foreach (var m in chronomorphs) fight.AddMonster(m);
                player.CellId = 300;
                julith.CellId = 330;
                for (int i = 0; i < 4; i++) chronomorphs[i].CellId = 334 + 2 * i;
                fight.StartFight();

                await FightHandler.AplicarEfectosAsync(null, fight, julith, 11966, 1, julith,
                                                       EffectEngine.AlLanzar, julith.CellId);

                var held = chronomorphs.Select(m => slayer.Where(m.Buffs.TieneEstado).ToList()).ToList();
                Assert.All(held, h => Assert.Single(h));
                Assert.Equal(4, held.Select(h => h[0]).Distinct().Count());
                Assert.All(chronomorphs, m => Assert.DoesNotContain(new[] { 960, 961, 962, 963 }, m.Buffs.TieneEstado));
            }
        }

        /// <summary>
        /// Qilby lays each islet's blue glyph (32143 grade 2 -> 32144) when the fight starts and
        /// again at his turn's end; laid again on the same spot, the glyph lasts on and does not
        /// stand twice on the same cells.
        /// </summary>
        [Fact]
        public async Task Qilby_lays_one_blue_glyph_per_islet_however_often_he_lays_it()
        {
            var player = Character(53000000033, 300);
            var qilby = Monster(-1, 330);
            qilby.MonsterId = 8131;
            var fight = new FightInstance(1, 1);
            fight.AddPlayer(player);
            fight.AddMonster(qilby);
            player.CellId = 300;
            qilby.CellId = 330;
            fight.StartFight();

            for (int i = 0; i < 2; i++)
                await FightHandler.AplicarEfectosAsync(null, fight, qilby, 32143, 2, qilby,
                                                       EffectEngine.AlLanzar, 200);

            var blue = fight.Glifos.Where(g => !g.Gastado && g.Hechizo == 32144).ToList();
            Assert.Single(blue);
            Assert.Equal(200, blue[0].Centro);
        }
    }
}
