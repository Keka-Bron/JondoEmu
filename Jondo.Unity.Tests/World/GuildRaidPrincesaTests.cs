using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Princesa Maldita's fight: her Flor Maldita in the middle of her board, its body four cells,
    /// and her Aura turning to statues, from her second round, whoever she sees as her turn starts.
    /// In the MapManager collection, as every test that loads the maps.
    /// </summary>
    [Collection("MapManager")]
    public class GuildRaidPrincesaTests
    {
        private const long Top = 238297364;
        private const long Arena = 238297361;

        private static bool HasData => GuildRaidPrincesa.FlorBodyCells().Count == 3;

        /// <summary>The Flor's body: the three cells its "createHitbox" names, and the one closing their square, its own.</summary>
        [Fact]
        public void The_flor_s_body_is_four_cells()
        {
            if (!HasData) return;
            if (!MapManager.WalkableCells.ContainsKey(Arena)) MapManager.Initialize();
            Assert.Equal(new[] { 355, 369, 370 }, GuildRaidPrincesa.FlorBodyCells().OrderBy(c => c));
            Assert.Equal(383, GuildRaidPrincesa.FlorCell(Arena));
            Assert.Equal(Arena, MapManager.ResolveArenaMapId(Top));
        }

        private static async Task<(FightInstance Fight, Fighter Princesa, Fighter[] People)> Fight(params int[] cells)
        {
            if (!MapManager.WalkableCells.ContainsKey(Arena)) MapManager.Initialize();
            MobSpawnManager.EnsureMonsterData();
            var fight = new FightInstance(9801 + cells.Length, Top, Arena);
            var people = cells.Select((c, i) => new Fighter
            {
                Id = 7201 + i, TeamId = 0, CellId = c, MaxHP = 5000, CurrentHP = 5000, Level = 200, Initiative = 10 - i,
            }).ToArray();
            foreach (var person in people) fight.AddPlayer(person);
            var princesa = new Fighter { Id = -1, TeamId = 1, CellId = 268, MaxHP = 50000, CurrentHP = 50000, IsMonster = true,
                                         MonsterId = GuildRaidPrincesa.Princesa, Level = 200, Initiative = 100 };
            fight.AddMonster(princesa);
            GuildRaidPrincesa.OnFightCreated(fight);
            fight.StartFight();
            princesa.Conducta = (32003, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, princesa);
            await GuildRaidPrincesa.OnFightStartedAsync(null, fight);
            return (fight, princesa, people);
        }

        /// <summary>
        /// The Flor comes in on its cell, its body on the other three; a blow on one of those reaches
        /// it, and nobody can be put on them.
        /// </summary>
        [Fact]
        public async Task The_flor_stands_in_the_middle_with_its_body()
        {
            if (!HasData) return;
            var (fight, princesa, people) = await Fight(443, 444);
            try
            {
                Assert.Same(princesa, fight.SceneStandIn);
                var flor = fight.Rojo.Single(m => m.MonsterId == GuildRaidPrincesa.Flor);
                Assert.Equal(383, flor.CellId);
                Assert.Equal(new[] { 355, 369, 370 }, flor.ExtraCells.OrderBy(c => c));
                Assert.True(flor.EsInvocado);
                Assert.True(flor.Occupies(369));

                // A kill on a point, aimed at one of its body's cells.
                await FightHandler.CastAtAsync(null, fight, people[0], 31979, 2, 369);
                Assert.False(flor.IsAlive);
            }
            finally
            {
                GuildRaidPrincesa.Forget(fight.FightId);
            }
        }

        /// <summary>
        /// Her Aura holds in her first round; from her second, whoever she sees as her turn starts
        /// becomes a statue and passes, whoever something hides does not; at her next turn start
        /// the statues die.
        /// </summary>
        [Fact]
        public async Task Her_aura_turns_whoever_she_sees_to_stone()
        {
            if (!HasData) return;
            if (!MapManager.WalkableCells.ContainsKey(Arena)) MapManager.Initialize();
            // One in her sight, one with the other right in between (a fighter hides a fighter).
            var walkable = MapManager.GetFightWalkable(Arena);
            var blockers = MapManager.GetLosBlockers(Arena);
            int seen = walkable.Where(c => MapGeometry.Distance(c, 268) == 3 && MapGeometry.HasLineOfSight(268, c, blockers)).OrderBy(c => c).First();
            int shield = -1, hidden = -1;
            foreach (int c1 in walkable.Where(c => c != seen && MapGeometry.Distance(c, 268) <= 2).OrderBy(c => c))
            {
                foreach (int c2 in walkable.Where(c => c != seen && c != c1 && MapGeometry.Distance(c, 268) <= 5).OrderBy(c => c))
                {
                    if (!MapGeometry.HasLineOfSight(268, c2, blockers)) continue;
                    if (MapGeometry.HasLineOfSight(268, c2, c => blockers.Contains(c) || c == c1)) continue;
                    if (!MapGeometry.HasLineOfSight(268, c1, blockers)) continue;
                    shield = c1; hidden = c2;
                    break;
                }
                if (shield >= 0) break;
            }
            Assert.True(shield >= 0);
            var (fight, princesa, people) = await Fight(seen, shield, hidden);
            // Her turn starting in a round, as a fight starts it: what waited for the round, then "TB".
            async Task HerTurnIn(int round)
            {
                while (fight.RoundNumber < round) fight.NextTurn();
                await FightHandler.ApplyDuePendingAsync(null, fight);
                await FightHandler.DispararAsync(null, fight, princesa, EffectEngine.AlEmpezarElTurno);
            }
            try
            {
                await HerTurnIn(1);
                Assert.DoesNotContain(people, p => p.Buffs.TieneEstado(6650));   // not in her first round

                await HerTurnIn(2);
                Assert.True(people[0].Buffs.TieneEstado(6650));
                Assert.True(people[1].Buffs.TieneEstado(6650));                  // the one in front, seen
                Assert.False(people[2].Buffs.TieneEstado(6650));                 // hidden behind him

                await HerTurnIn(3);                                              // her Aura still on, three rounds in
                Assert.False(people[0].IsAlive);
                Assert.True(people[2].IsAlive);
            }
            finally
            {
                GuildRaidPrincesa.Forget(fight.FightId);
            }
        }

        /// <summary>
        /// The Flor at its threshold: healed in full, over to the players for two rounds, invulnerable,
        /// one of their side in the order, healing them at its turn's end; then back, open to blows, its
        /// threshold on again -- for good.
        /// </summary>
        [Fact]
        public async Task The_flor_goes_over_to_the_players_for_two_rounds()
        {
            if (!HasData) return;
            var (fight, princesa, people) = await Fight(443, 444);
            try
            {
                var flor = fight.Rojo.Single(m => m.MonsterId == GuildRaidPrincesa.Flor);
                Assert.Contains(flor.Buffs.Puestos, r => r.EffectId == EffectEngine.Umbral);
                flor.CurrentHP = 1;
                await FightHandler.DispararAsync(null, fight, flor, "TR32020");

                Assert.Equal(flor.MaxHP, flor.CurrentHP);
                Assert.Equal(FightInstance.Azules, flor.TeamId);
                Assert.Contains(flor, fight.Azul);
                Assert.DoesNotContain(flor, fight.Rojo);
                Assert.Contains(flor, fight.TurnOrder);
                Assert.True(flor.Buffs.TieneEstado(56));
                Assert.True(princesa.Buffs.TieneEstado(6664));

                people[0].CurrentHP = 1000;
                await FightHandler.DispararAsync(null, fight, flor, EffectEngine.AlAcabarElTurno);
                Assert.True(people[0].CurrentHP > 1000);                           // its petal heals its camp

                // Every turn start of that round, as the fight runs them: the wait is its caster's.
                int back = fight.RoundNumber + 2;
                while (fight.RoundNumber < back) fight.NextTurn();
                while (fight.RoundNumber == back && flor.TeamId != FightInstance.Rojos)
                {
                    await FightHandler.ApplyDuePendingAsync(null, fight);
                    if (flor.TeamId != FightInstance.Rojos) fight.NextTurn();
                }
                Assert.Equal(FightInstance.Rojos, flor.TeamId);
                Assert.Contains(flor, fight.Rojo);
                Assert.False(flor.Buffs.TieneEstado(56));
                Assert.True(princesa.Buffs.TieneEstado(6665));

                // Its threshold on again, and still there rounds later.
                int later = fight.RoundNumber + 3;
                while (fight.RoundNumber < later) fight.NextTurn();
                flor.CurrentHP = 1;
                await FightHandler.DispararAsync(null, fight, flor, "TR32020");
                Assert.Equal(FightInstance.Azules, flor.TeamId);
            }
            finally
            {
                GuildRaidPrincesa.Forget(fight.FightId);
            }
        }

        /// <summary>
        /// Her phase II: healed, invulnerable, twice a round, the Volonté de la Reine beside her; the
        /// Flor's turn lays the purple glyph on the ring; its stepper gets Encantamiento Floral, a
        /// spell of one use; cast on her twice, it takes her invulnerability.
        /// </summary>
        [Fact]
        public async Task The_purple_glyph_s_spell_breaks_her_invulnerability()
        {
            if (!HasData) return;
            var (fight, princesa, people) = await Fight(443, 444);
            try
            {
                princesa.CurrentHP = 1;
                await FightHandler.DispararAsync(null, fight, princesa, "TR32013");   // her 1-life threshold
                Assert.True(GuildRaidPrincesa.InSecondPhase(fight));
                Assert.Equal(princesa.MaxHP, princesa.CurrentHP);
                Assert.True(princesa.Buffs.TieneEstado(56));
                Assert.Equal(50, fight.TwiceARound[princesa.Id]);
                var volonte = fight.Rojo.Single(m => m.MonsterId == GuildRaidPrincesa.Volonte);
                Assert.False(volonte.EsInvocado);

                var flor = fight.Rojo.Single(m => m.MonsterId == GuildRaidPrincesa.Flor);
                var player = people[0];
                for (int use = 1; use <= 2; use++)
                {
                    await GuildRaidPrincesa.OnTurnStartAsync(null, fight, flor);
                    var glyph = fight.Glifos.Single(g => g.Dueno == flor.Id);
                    Assert.Contains(glyph.Casillas.First(), GuildRaidPrincesa.GlyphCells());

                    // Stepped on: the spell given, the glyph gone.
                    await FightHandler.AplicarEfectosAsync(null, fight, flor, glyph.Hechizo, glyph.Grado, player,
                                                           EffectEngine.AlLanzar, player.CellId);
                    Assert.Contains(player.GrantedSpells, g => g.Spell == 32024);
                    Assert.DoesNotContain(fight.Glifos, g => g.Dueno == flor.Id);

                    await FightHandler.CastAtAsync(null, fight, player, 32024, 1, princesa.CellId);
                    Assert.DoesNotContain(player.GrantedSpells, g => g.Spell == 32024);   // one use
                    Assert.Contains(32024, player.TakenSpells);
                    Assert.True(princesa.Buffs.TieneEstado(GuildRaidPrincesa.Vulnerable));
                    Assert.Equal(use == 1, princesa.Buffs.TieneEstado(56));
                }
            }
            finally
            {
                GuildRaidPrincesa.Forget(fight.FightId);
            }
        }
    }
}
