using System.Collections.Generic;
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
    /// The Wa Wabbit Castle (dungeon 52): the Wo Wabbit's summons, the Wabbit's escape boost,
    /// the boss's shield and the harness's reading of a spell split on the caster's state.
    /// </summary>
    [Collection("MapManager")]
    public class WaWabbitCastleTests
    {
        private const long CastleMap = 116392455;
        private const int WoWabbit = 97;
        private const int KingWabbit = 72;
        private const int WaWabbit = 180;
        private const int Carrot = 7020;

        [Fact]
        public void The_Wo_Wabbit_keeps_two_summons_at_most()
        {
            MonsterSummonCaps.Reload();
            var wo = new Fighter { Id = -1, TeamId = 1, IsMonster = true, MonsterId = WoWabbit, Level = 50 };
            Assert.Equal(2, FightHandler.SummonLimitFor(wo, round: 1));
        }

        [Theory]
        [InlineData(5, 327)]
        [InlineData(1, 3783)]
        public void The_escape_boost_is_worth_casting_only_when_tackled(int distance, int expected)
        {
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            var cells = MapManager.GetFightWalkable(CastleMap);
            bool Free(int c) => MapGeometry.IsValid(c) && (cells?.Contains(c) ?? MapManager.IsCellWalkable(CastleMap, c));
            var all = Enumerable.Range(0, MapGeometry.MaxCells).ToList();
            int playerCell = all.First(c => Free(c) && all.Count(b => Free(b) && MapGeometry.Distance(b, c) == distance) > 2
                                            && MapGeometry.GetNeighbors(c).Count(Free) == 4);
            int monsterCell = all.First(b => Free(b) && MapGeometry.Distance(b, playerCell) == distance);

            var fight = new FightInstance(9_999_052, CastleMap, CastleMap);
            var wabbit = new Fighter { Id = -1, TeamId = 1, CellId = monsterCell, IsMonster = true, MonsterId = KingWabbit,
                                       Level = 50, MaxHP = 500, CurrentHP = 500, MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3 };
            foreach (int s in new[] { 327, 3783 }) { wabbit.SpellIds.Add(s); wabbit.SpellGrades[s] = 1; }
            var player = new Fighter { Id = 10, TeamId = 0, CellId = playerCell, Level = 60, MaxHP = 2000, CurrentHP = 2000,
                                       MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3 };
            fight.AddPlayer(player);
            fight.AddOpponent(wabbit);

            var next = MonsterTactics.Next(FightHandler.BoardOf(fight), wabbit, FightHandler.TacticsOf(wabbit));

            Assert.Equal(expected, next?.Spell.Id);
        }

        private static (FightInstance Fight, Fighter Boss) ShieldedBoss()
        {
            var fight = new FightInstance(1, 1);
            var player = new Fighter { Id = 10, Level = 60, CellId = 200, MaxHP = 2000, CurrentHP = 2000 };
            var boss = new Fighter { Id = -1, IsMonster = true, MonsterId = WaWabbit, Level = 60, CellId = 300,
                                     MaxHP = 3000, CurrentHP = 3000 };
            fight.AddPlayer(player);
            fight.AddMonster(boss);
            boss.Buffs.PonerEstado(Carrot);
            return (fight, boss);
        }

        private static void CastShield(FightInstance fight, Fighter boss)
            => EffectEngine.Resolver(fight, boss, 3780, 1, boss, EffectEngine.AlLanzar, 0, celdaApuntada: boss.CellId);

        [Fact]
        public void The_boss_shield_lives_as_long_as_its_row()
        {
            var (fight, boss) = ShieldedBoss();
            CastShield(fight, boss);
            Assert.Equal(120, boss.PuntosDeEscudo); // 200% of level 60
            var row = boss.Buffs.Puestos.Single(b => b.EffectId == EffectEngine.ShieldPanelEffect);

            // The round the row falls in starts with somebody else's turn: the shield still holds.
            for (int i = 0; i < 20 && fight.RoundNumber < row.CaducaEnRonda; i++) fight.NextTurn();
            Assert.Equal(row.CaducaEnRonda, fight.RoundNumber);
            Assert.Contains(row, boss.Buffs.Puestos);
            Assert.Equal(120, boss.PuntosDeEscudo);
            Assert.Equal(794, boss.PasarPorElEscudo(914));

            // And it goes with the row, at its caster's turn start.
            FightHandler.TurnStartSweep(fight, boss);
            Assert.DoesNotContain(row, boss.Buffs.Puestos);
            Assert.Equal(0, boss.PuntosDeEscudo);
        }

        [Fact]
        public void A_recast_over_a_spent_shield_gives_its_points()
        {
            var (fight, boss) = ShieldedBoss();
            CastShield(fight, boss);
            Assert.Equal(0, boss.PasarPorElEscudo(120));
            Assert.Equal(0, boss.PuntosDeEscudo);

            CastShield(fight, boss);

            Assert.Single(boss.Buffs.Puestos, b => b.EffectId == EffectEngine.ShieldPanelEffect);
            Assert.Equal(120, boss.PuntosDeEscudo);
        }

        [Theory]
        [InlineData("A,*E7020", null, true)]
        [InlineData("A,*E7020", Carrot, false)]
        [InlineData("A,*e7020", Carrot, true)]
        [InlineData("A,*e7020", null, false)]
        [InlineData("A", null, false)]
        public void The_caster_condition_is_read_from_the_states_at_the_cast(string mask, int? state, bool failed)
        {
            var caster = new Fighter { Id = -1, IsMonster = true };
            var states = state is int s ? new HashSet<int> { s } : new HashSet<int>();
            Assert.Equal(failed, ControlApi.CasterConditionFailed(caster, mask, states));
        }

        [Fact]
        public void The_harness_calls_the_other_variant_of_338_conditional()
        {
            var fight = new FightInstance(1, 1);
            var player = new Fighter { Id = 10, Level = 60, CellId = 200, MaxHP = 2000, CurrentHP = 2000 };
            var boss = new Fighter { Id = -1, IsMonster = true, MonsterId = WaWabbit, Level = 60, CellId = 300,
                                     MaxHP = 3000, CurrentHP = 3000 };
            fight.AddPlayer(player);
            fight.AddMonster(boss);
            var before = fight.Todos.ToDictionary(f => f.Id, f => (f.CurrentHP, f.CurrentAP, f.CurrentMP, f.CellId, 1, f.IsAlive));
            var statesBefore = fight.Todos.ToDictionary(f => f.Id, f => new HashSet<int>(f.Buffs.Estados));

            // Without the carrot state only the *e7020 rows apply: Wabbit's power gives 150, not 300.
            var row = player.Buffs.Poner(new Buff { EffectId = 186, Cuanto = 150, HechizoOrigen = 338, NivelOrigen = 1,
                                                    Quien = boss.Id, Disparador = EffectEngine.AlLanzar, CaducaEnRonda = 2 },
                                         fight.SiguienteEmbrujo);
            var rows = new List<(Fighter Who, Buff Row)> { (player, row) };

            var verdicts = ControlApi.VerifyEffects(fight, boss, 338, 1, 1, before, statesBefore, rows, new List<Glifo>(),
                                                    f => f == boss ? "boss" : "player");

            string VerdictOf(int effect, string mask) => verdicts
                .Select(v => v.GetType())
                .Zip(verdicts, (t, v) => (Effect: (int)t.GetProperty("effect")!.GetValue(v)!,
                                          Mask: (string)t.GetProperty("TargetMask")!.GetValue(v)!,
                                          Verdict: (string)t.GetProperty("verdict")!.GetValue(v)!))
                .First(v => v.Effect == effect && v.Mask == mask).Verdict;
            Assert.Equal("conditional", VerdictOf(186, "A,*E7020"));
            Assert.NotEqual("conditional", VerdictOf(186, "A,*e7020"));
        }
    }
}
