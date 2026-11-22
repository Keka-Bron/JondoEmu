using System.Linq;
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
        public async Task From_afar_in_line_too(int attackerCell, int bossCell, long map, int bossState, int attackerState)
        {
            if (SpellEffects.De(Passive, 1).Count == 0) return;
            if (map > 1 && !MapManager.WalkableCells.ContainsKey(map)) MapManager.Initialize();
            var fight = new FightInstance(1, map);
            var boss = Boss(bossCell);
            var attacker = Person(10, attackerCell);
            fight.AddMonster(boss); fight.AddPlayer(attacker);
            fight.StartFight();
            boss.CellId = bossCell; attacker.CellId = attackerCell;
            boss.Conducta = (Passive, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, boss);
            // His Pulsar, which takes a share of what he is dealt (4935 on state 214).
            var pulsar = new Fighter { Id = -5, TeamId = 1, CellId = 401, MaxHP = 2000, CurrentHP = 2000, Level = 200,
                                       IsMonster = true, MonsterId = 3822, SummonCost = 1 };
            fight.Invocar(pulsar, boss);
            pulsar.CellId = 401;
            pulsar.Buffs.PonerEstado(214);
            boss.Buffs.PonerEstado(bossState);
            attacker.Buffs.PonerEstado(attackerState);
            // A Cra's arrow on him, as in the fight: the real blow, through his invulnerability.
            var arrow = SpellEffects.De(32453, 1).First(e => e.EffectId == 92);
            await FightHandler.UnGolpeAsync(null, fight, attacker, 32453, arrow, 4, boss, 30, 0, false);
            Assert.Equal(1, MapGeometry.Distance(boss.CellId, attacker.CellId));
        }
    }
}
