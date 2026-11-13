using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Giant Kralamoure's fight played through, as Dofus pour les noobs tells it: hit in an
    /// element, he summons that element's tentacle; the Primary eats a summon and the three others
    /// hit one, each passing a state to the next (31, 32, 33), the Quaternary's reaching him (34);
    /// with it he casts Vulnérabilité de la Tourbière and loses his 900 % for a turn.
    /// </summary>
    [Collection("MapManager")]
    public class KralamoureFightTests
    {
        private const long BossRoom = 26740736;
        private const int Kralamoure = 423, KralamoureCell = 344;
        private const int Kraken = 1103, Vulnerabilidad = 1106;
        private const int Primario = 1107, Secundario = 1108, Terciario = 1109, Cuaternario = 1110;
        private const int KrakenPrimario = 1096, KrakenSecundario = 1097, KrakenTerciario = 1098, KrakenCuaternario = 1099;

        private readonly StringBuilder _trace = new();

        [Fact]
        public async Task The_chain_of_tentacles_makes_him_vulnerable()
        {
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            if (!MapManager.WalkableCells.TryGetValue(BossRoom, out var walkable)) return;   // no world data
            MobSpawnManager.EnsureMonsterData();

            var fight = new FightInstance(9_999_779, BossRoom, BossRoom);
            var player = new Fighter
            {
                Id = 7301, TeamId = 0, CellId = 549, MaxHP = 50000, CurrentHP = 50000, Level = 200,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Initiative = 10,
            };
            var kralamoure = new Fighter
            {
                Id = -1, TeamId = 1, CellId = KralamoureCell, MaxHP = 5800, CurrentHP = 5800, Level = 200, IsMonster = true,
                MonsterId = Kralamoure, MaxAP = 18, CurrentAP = 18, Initiative = 100,
                EarthResPct = 900, FireResPct = 900, WaterResPct = 900, AirResPct = 900, NeutralResPct = 900,
            };
            fight.AddPlayer(player);
            fight.AddMonster(kralamoure);
            fight.StartFight();

            // Two cells for the player's summons, four cells or more from him: one for the Primary
            // to eat, one with three free neighbours the three others hit it from.
            var free = new HashSet<int>(walkable.Where(c => MapGeometry.Distance(KralamoureCell, c) >= 4 && c != player.CellId));
            int eaten = free.First(c => MapGeometry.GetNeighbors(c).Count(free.Contains) >= 1);
            int hit = free.First(c => c != eaten && MapGeometry.Distance(c, eaten) > 3
                                      && MapGeometry.GetNeighbors(c).Count(n => free.Contains(n) && n != eaten) >= 3);
            var around = MapGeometry.GetNeighbors(hit).Where(n => free.Contains(n)).Take(3).ToArray();
            int byEaten = MapGeometry.GetNeighbors(eaten).First(n => free.Contains(n) && n != hit && !around.Contains(n));
            Trace($"cells: eaten {eaten} (primary {byEaten}), hit {hit} (others {string.Join(",", around)})");

            await Cast(fight, kralamoure, Kraken, 1, kralamoure);
            Trace($"after Kraken: {kralamoure.Buffs.ActiveSpells.Count} hook(s) " +
                  string.Join(" ", kralamoure.Buffs.ActiveSpells.Select(a => $"[{a.Hechizo} rows {(a.Filas == null ? "all" : a.Filas.Count.ToString())} until {a.CaducaEnRonda}]")) +
                  $"; buffs {string.Join(",", kralamoure.Buffs.Puestos.Select(b => b.EffectId))}; round {fight.RoundNumber}");

            // Each element's state -- what Kraken gives him when hit in it, seen in the game --, and
            // its state its tentacle.
            var tentacles = new Dictionary<int, Fighter>();
            foreach (var (state, summon, cell) in new[]
                     {
                         (37, Terciario, around[0]), (38, Secundario, around[1]),
                         (35, Primario, byEaten), (36, Cuaternario, around[2]),
                     })
            {
                foreach (int other in new[] { 35, 36, 37, 38 }) kralamoure.Buffs.QuitarEstadoConEmbrujos(other);
                kralamoure.Buffs.PonerEstado(state);
                Assert.True(SpellCriteria.Allows(kralamoure, summon, 1), _trace.ToString());

                await CastAtCell(fight, kralamoure, summon, 1, cell);
                var tentacle = fight.Rojo.FirstOrDefault(f => f.IsAlive && f.CellId == cell);
                Trace($"summon {summon} on {cell}: {(tentacle == null ? "nobody" : $"{tentacle.Id} template {tentacle.MonsterId}")}");
                Assert.NotNull(tentacle);
                tentacles[summon] = tentacle!;
            }

            var meal = Summon(fight, player, -100, eaten);
            var wall = Summon(fight, player, -101, hit);

            // The relay: the Primary eats, then each passes its state on.
            await Relay(fight, tentacles[Primario], KrakenPrimario, meal, tentacles[Secundario], 31);
            Assert.False(meal.IsAlive);
            await Relay(fight, tentacles[Secundario], KrakenSecundario, wall, tentacles[Terciario], 32);
            await Relay(fight, tentacles[Terciario], KrakenTerciario, wall, tentacles[Cuaternario], 33);
            await Relay(fight, tentacles[Cuaternario], KrakenCuaternario, wall, kralamoure, 34);

            Assert.True(SpellCriteria.Allows(kralamoure, Vulnerabilidad, 1));
            await Cast(fight, kralamoure, Vulnerabilidad, 1, kralamoure);

            var rows = kralamoure.Buffs.Puestos.Where(b => b.HechizoOrigen == Vulnerabilidad).ToList();
            Trace($"vulnerability rows on him: {string.Join(" ", rows.Select(b => $"[effect {b.EffectId} characteristic {b.Caracteristica} {b.Cuanto}]"))}; " +
                  $"resistance the blow reads: {FightHandler.ResistenciaDeLaVictima(kralamoure, ElementType.Water, fight.RoundNumber)}");
            System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kralamoure_fight.txt"), _trace.ToString());
            Assert.Contains(rows, b => b.EffectId == 1077);
            // His 900 % gone for the turn: the blows reach him.
            Assert.Equal(0, FightHandler.ResistenciaDeLaVictima(kralamoure, ElementType.Water, fight.RoundNumber));
        }

        private async Task Relay(FightInstance fight, Fighter tentacle, int spell, Fighter on, Fighter next, int state)
        {
            bool allowed = SpellCriteria.Allows(tentacle, spell, 5);
            tentacle.CellId = MapGeometry.GetNeighbors(on.CellId).FirstOrDefault(c => c == tentacle.CellId, tentacle.CellId);
            await Cast(fight, tentacle, spell, 5, on);
            Trace($"{tentacle.MonsterId} casts {spell} (allowed {allowed}) on {on.Id}: {next.Id} has {state}: {next.Buffs.TieneEstado(state)}");
            Assert.True(allowed, $"{spell} allowed");
            Assert.True(next.Buffs.TieneEstado(state), _trace.ToString());
        }

        private static Fighter Summon(FightInstance fight, Fighter owner, long id, int cell)
        {
            var summon = new Fighter
            {
                Id = id, TeamId = owner.TeamId, CellId = cell, MaxHP = 1890, CurrentHP = 1890, Level = 200,
                IsMonster = true, MonsterId = 5833, Invocador = owner.Id,
            };
            fight.AddPlayer(summon);
            return summon;
        }

        private static Task Cast(FightInstance fight, Fighter caster, int spell, int grade, Fighter target)
            => FightHandler.AplicarEfectosAsync(null, fight, caster, spell, grade, target, EffectEngine.AlLanzar, target.CellId);

        private static Task CastAtCell(FightInstance fight, Fighter caster, int spell, int grade, int cell)
            => FightHandler.AplicarEfectosAsync(null, fight, caster, spell, grade, null, EffectEngine.AlLanzar, cell);

        private void Trace(string line) => _trace.AppendLine(line);
    }
}
