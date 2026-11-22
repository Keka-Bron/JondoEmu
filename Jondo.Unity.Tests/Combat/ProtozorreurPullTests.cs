using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Protozorreur (3828), Krobe's Lair. His passive 4972 holds, under D, a 1019 that has the one who
    /// hit him cast 4967: a forced pull (1022) of the boss, in a straight cross around the attacker,
    /// to contact. The first damage he takes in line each turn brings him next to its dealer.
    /// </summary>
    [Collection("MapManager")]
    public class ProtozorreurPullTests
    {
        private const int Protozorreur = 3828;
        private const int Passive = 4972;
        private const int BossCell = 215;

        private static Fighter Person(long id, int cell) => new()
        {
            Id = id, TeamId = 0, CellId = cell, MaxHP = 5000, CurrentHP = 5000, Level = 200,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
        };

        private static Fighter Boss(int cell) => new()
        {
            Id = -1, TeamId = 1, CellId = cell, MaxHP = 20000, CurrentHP = 20000, Level = 200,
            IsMonster = true, MonsterId = Protozorreur, MaxAP = 12, CurrentAP = 12, MaxMP = 0, CurrentMP = 0,
        };

        private static (Fighter Boss, Fighter Attacker) HitFrom(int attackerCell, int bossCell = BossCell,
                                                                 bool asAtTheStart = false)
        {
            var fight = new FightInstance(1, 1);
            var boss = Boss(bossCell);
            var attacker = Person(10, attackerCell);
            fight.AddMonster(boss); fight.AddPlayer(attacker);
            boss.CellId = bossCell; attacker.CellId = attackerCell;
            boss.Buffs.PonerActitud(Passive);
            if (asAtTheStart)
                EffectEngine.Resolver(fight, boss, Passive, 1, boss, EffectEngine.AlLanzar, fight.RoundNumber,
                                      celdaApuntada: boss.CellId);

            fight.TriggeringAttacker = attacker;
            EffectEngine.Resolver(fight, boss, Passive, 1, boss, EffectEngine.AlRecibirDano, fight.RoundNumber);
            fight.TriggeringAttacker = null;
            return (boss, attacker);
        }

        [Fact]
        public void A_hit_in_line_pulls_him_to_the_attacker()
        {
            if (SpellEffects.De(Passive, 1).Count == 0) return;   // no spell data
            int inLine = Zone.Casillas('Q', 4, BossCell, BossCell, 4).First(MapGeometry.IsValid);
            var (boss, attacker) = HitFrom(inLine);
            Assert.Equal(1, MapGeometry.Distance(boss.CellId, attacker.CellId));
        }

        [Fact]
        public void A_hit_off_the_lines_leaves_him_where_he_is()
        {
            if (SpellEffects.De(Passive, 1).Count == 0) return;
            var (boss, _) = HitFrom(BossCell - 28);   // one diagonal step
            Assert.Equal(BossCell, boss.CellId);
        }
        [Fact]
        public async Task His_behaviour_spell_pulls_him_when_hit_in_line()
        {
            if (SpellEffects.De(Passive, 1).Count == 0) return;
            int inLine = Zone.Casillas('Q', 4, BossCell, BossCell, 4).First(MapGeometry.IsValid);
            var fight = new FightInstance(1, 1);
            var boss = Boss(BossCell);
            var attacker = Person(10, inLine);
            fight.AddMonster(boss); fight.AddPlayer(attacker);
            fight.StartFight();
            boss.CellId = BossCell; attacker.CellId = inLine;
            boss.Conducta = (Passive, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, boss);

            fight.TriggeringAttacker = attacker;
            await FightHandler.DispararAsync(null, fight, boss, EffectEngine.AlRecibirDano);
            fight.TriggeringAttacker = null;
            Assert.Equal(1, MapGeometry.Distance(boss.CellId, attacker.CellId));
        }
        [Theory]
        [InlineData(282, 427, 1, 0, 0)]
        [InlineData(282, 427, 239606295, 0, 0)]
        [InlineData(282, 427, 1, 214, 0)]
        [InlineData(282, 427, 1, 0, 56)]
        [InlineData(282, 427, 1, 0, 0, 2)]
        [InlineData(282, 427, 1, 0, 0, 1, true)]
        [InlineData(282, 427, 239606295, 0, 0, 2, true)]
        public async Task From_afar_in_line_too(int attackerCell, int bossCell, long map, int bossState, int attackerState,
                                                int grade = 1, bool audacia = false, int pulsarCell = 401)
        {
            if (SpellEffects.De(Passive, 1).Count == 0) return;
            if (map > 1 && !MapManager.WalkableCells.ContainsKey(map)) MapManager.Initialize();
            var fight = new FightInstance(1, map);
            var boss = Boss(bossCell);
            var attacker = Person(10, attackerCell);
            fight.AddMonster(boss); fight.AddPlayer(attacker);
            fight.StartFight();
            boss.CellId = bossCell; attacker.CellId = attackerCell;
            if (audacia)
            {
                // His expedition modifier, held and cast at the start as every attitude is.
                const int AudaciaSpell = 23935;
                boss.Buffs.PonerActitud(AudaciaSpell);
                await FightHandler.AplicarEfectosAsync(null, fight, boss, AudaciaSpell, 1, boss,
                                                       EffectEngine.AlLanzar, boss.CellId);
            }
            boss.Conducta = (Passive, grade);
            await FightHandler.LanzarLaConductaAsync(null, fight, boss);
            // His Pulsar, which takes a share of what he is dealt (4935 on state 214).
            var pulsar = new Fighter { Id = -5, TeamId = 1, CellId = pulsarCell, MaxHP = 2000, CurrentHP = 2000, Level = 200,
                                       IsMonster = true, MonsterId = 3822, SummonCost = 1 };
            fight.Invocar(pulsar, boss);
            pulsar.CellId = pulsarCell;
            pulsar.Buffs.PonerEstado(214);
            boss.Buffs.PonerEstado(bossState);
            attacker.Buffs.PonerEstado(attackerState);
            // A Cra's arrow on him, as in the fight: the real blow, through his invulnerability.
            var arrow = SpellEffects.De(32453, 1).First(e => e.EffectId == 92);
            await FightHandler.UnGolpeAsync(null, fight, attacker, 32453, arrow, 4, boss, 30, 0, false);
            Assert.Equal(1, MapGeometry.Distance(boss.CellId, attacker.CellId));
        }
        [Fact]
        public async Task His_hooks_outlive_the_real_start_of_the_fight()
        {
            if (SpellEffects.De(Passive, 1).Count == 0) return;
            const long person = 8_300_000_177;
            const long map = 188744196;
            await using var pipe = await Jondo.Unity.Tests.Economy.ClientPipe.OpenAsync(8377, person, map, "P");
            // His own room, which the expedition fights on: its ground decides what a pull crosses.
            const long room = 239606295;
            if (!MapManager.WalkableCells.ContainsKey(room)) MapManager.Initialize();
            var fight = new FightInstance(9377, map, room) { Reglas = FightRules.ContraMonstruos };
            fight.SetPlacementCells(new[] { 300 }, new[] { 180 });
            var attacker = Person(person, 300);
            fight.AddPlayer(attacker);
            var boss = Boss(180);
            boss.Conducta = (Passive, 1);
            boss.Buffs.PonerActitud(23935);
            fight.AddMonster(boss);
            fight.SetFighterReady(person);
            var drain = System.Threading.Tasks.Task.Run(async () =>
            {
                try { while (await Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer) != null) { } } catch { }
            });
            var active = (System.Collections.Concurrent.ConcurrentDictionary<long, FightInstance>)typeof(FightHandler)
                .GetField("_activeFights", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
            Assert.True(active.TryAdd(fight.FightId, fight));
            try
            {
                using (SessionContext.Push(pipe.Session)) await FightHandler.StartFightAsync(fight);
                Assert.Contains(boss.Buffs.ActiveSpells, h => h.Hechizo == Passive);
                // Turns pass as the server passes them, his own among them.
                for (int i = 0; i < 6 && fight.RoundNumber < 4; i++)
                {
                    using (SessionContext.Push(pipe.Session))
                    {
                        await FightHandler.PassTurnAsync(pipe.Session.Stream);
                        await FightHandler.ConfirmAsync(pipe.Session.Stream);
                    }
                    Assert.True(boss.Buffs.ActiveSpells.Any(h => h.Hechizo == Passive),
                                $"the hook went in round {fight.RoundNumber}, turn of {fight.CurrentFighter?.Id}");
                }
                Assert.True(fight.RoundNumber >= 2, "no turn passed: round " + fight.RoundNumber);

            // And they still answer a real blow in line: ten cells along his row, an arrow.
            foreach (var other in fight.Todos.Where(x => x != boss && x != attacker)) other.CellId = 13;
            boss.CellId = 427;
            attacker.CellId = 282;
            Assert.Equal(MapGeometry.CellToPoint(427).Y, MapGeometry.CellToPoint(282).Y);
            var arrow = SpellEffects.De(32453, 1).First(e => e.EffectId == 92);
            using (SessionContext.Push(pipe.Session))
                await FightHandler.UnGolpeAsync(pipe.Session.Stream, fight, attacker, 32453, arrow, 4, boss, 30, 0, false);
            Assert.Equal(1, MapGeometry.Distance(boss.CellId, attacker.CellId));
            // His "1160 4968 on L under D" is armed on the enemy players: a blow to the player,
            // not to him, lays one Infection glyph (4966) under that player.
            Assert.Contains(attacker.Buffs.ActiveSpells, h => h.Hechizo == Passive);
            Assert.DoesNotContain(fight.Glifos, g => g.Hechizo == 4966);
            using (SessionContext.Push(pipe.Session))
                await FightHandler.UnGolpeAsync(pipe.Session.Stream, fight, boss, 32453, arrow, 4, attacker, 30, 0, false);
            Assert.Single(fight.Glifos, g => g.Hechizo == 4966 && g.Casillas.Contains(attacker.CellId));
            }
            finally
            {
                active.TryRemove(fight.FightId, out _);
                fight.CancelTurnTimer();
            }
        }
    }
}
