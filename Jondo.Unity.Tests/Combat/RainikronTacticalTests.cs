using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The tactical fight of «Le bonheur est dans le spray» (1911): the character fights as a
    /// Minikron (4947: one life, no MP, «12 PA» -- Dofus pour les noobs) against Rainikron (4946,
    /// four life points). Her behaviour (level 34048, spell 8917) walks the board laying the
    /// coloured runes of 8916 and four pink ones (grade 5) on cells 160, 295, 305 and 440; the
    /// Minikron's step (Pirueta, 8922) jumps one cell and makes her set off the rune he lands on.
    /// A pink rune's 8916 grade 5 hits its caster for one neutral point: four pinks, and she is dead.
    /// </summary>
    [Collection("MapManager")]
    public class RainikronTacticalTests
    {
        private const int Rainikron = 4946, Minikron = 4947, Step = 8922, RuneSpell = 8916;
        private const int Pink = 14418430;
        private const long RoleplayMap = 180094978, Board = 183243783;
        private static readonly int[] PinkCells = { 160, 295, 305, 440 };

        private readonly ITestOutputHelper _out;
        public RainikronTacticalTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public void The_Minikron_fights_with_the_AP_its_fight_gives_it()
        {
            MobSpawnManager.EnsureMonsterData();
            if (MobSpawnManager.GetMonsterData(Minikron) is not { Grades.Count: > 0 }) return;   // no monster data

            var asIs = new Fighter { Id = 7401, TeamId = 0, MaxAP = 11, CurrentAP = 11, MaxMP = 5, CurrentMP = 5, MaxHP = 9000, CurrentHP = 9000 };
            Assert.True(FightHandler.Incarnate(asIs, Minikron, 0, 0));
            Assert.Equal(20, asIs.MaxAP);   // the grade's own sheet

            var told = new Fighter { Id = 7402, TeamId = 0, MaxAP = 11, CurrentAP = 11, MaxMP = 5, CurrentMP = 5, MaxHP = 9000, CurrentHP = 9000 };
            Assert.True(FightHandler.Incarnate(told, Minikron, 0, 0, actionPoints: 12));
            Assert.Equal(12, told.MaxAP);
            Assert.Equal(12, told.CurrentAP);
            Assert.Equal(0, told.MaxMP);
            Assert.Equal(1, told.MaxHP);
        }

        [Fact]
        public void A_row_s_incarnation_reads_its_action_points()
        {
            using var doc = JsonDocument.Parse(
                "{\"npc\": 1, \"reply\": 2, \"monsters\": [{\"monster\": 4946, \"grade\": 1}]," +
                " \"incarnates\": {\"monster\": 4947, \"grade\": 1, \"actionPoints\": 12}}");
            var fight = NpcFights.Parse(doc.RootElement);
            Assert.Equal(Minikron, fight.Incarnates);
            Assert.Equal(12, fight.IncarnationActionPoints);

            using var plain = JsonDocument.Parse("{\"npc\": 1, \"reply\": 2, \"incarnates\": {\"monster\": 4947, \"grade\": 1}}");
            Assert.Equal(0, NpcFights.Parse(plain.RootElement).IncarnationActionPoints);
        }

        [Fact]
        public async Task Four_pink_runes_stepped_on_kill_Rainikron()
        {
            if (!SpellEffects.De(RuneSpell, 5).Any(row => row.EffectId == 100)) return;   // no spell data
            MobSpawnManager.EnsureMonsterData();
            var data = MobSpawnManager.GetMonsterData(Rainikron);
            if (data == null || MobSpawnManager.GetMonsterData(Minikron) == null) return;

            if (MapManager.GetFightWalkable(Board) == null) MapManager.Initialize();
            var floor = MapManager.GetFightWalkable(Board);
            if (floor == null) return;   // no map data
            var fight = new FightInstance(9_904_946, RoleplayMap, Board);
            var me = new Fighter { Id = 7403, TeamId = 0, CellId = 300, Level = 200, MaxHP = 9000, CurrentHP = 9000 };
            Assert.True(FightHandler.Incarnate(me, Minikron, 0, 0, actionPoints: 12));
            fight.AddPlayer(me);
            var lady = FightHandler.BuildMonsterFighter(new MobSpawnManager.MobMember { Monster = data, GradeIndex = 0 }, -1, 141);
            fight.AddMonster(lady);
            fight.StartFight();
            await FightHandler.LanzarLaConductaAsync(null, fight, lady);

            var runes = fight.Glifos.Where(g => g.Tipo == 2022).ToList();
            _out.WriteLine($"runes {runes.Count}, pinks at {string.Join(",", runes.Where(g => g.Color == Pink).SelectMany(g => g.Casillas))}, lady {lady.CellId} hp {lady.CurrentHP}");
            _out.WriteLine($"fighters: {string.Join("; ", fight.Todos.Select(f => $"{f.Id}/{f.MonsterId}@{f.CellId} hp{f.CurrentHP}"))}");
            // Her walk covers the board: every cell of its floor nobody stands on gets a rune --
            // the Minikron is on 300, she is back on 141 and her three summons on 152, 449 and 460.
            var covered = runes.SelectMany(g => g.Casillas).ToHashSet();
            Assert.All(covered, c => Assert.Contains(c, floor));
            Assert.Equal(floor.Where(c => !fight.Todos.Any(f => f.IsAlive && f.CellId == c)).OrderBy(c => c), covered.OrderBy(c => c));
            foreach (int pink in PinkCells)
                Assert.Contains(runes, g => g.Color == Pink && g.Cubre(pink));

            int life = lady.CurrentHP;
            foreach (int pink in PinkCells)
            {
                int from = MapGeometry.GetNeighbors(pink).First(c => floor.Contains(c) && !fight.Todos.Any(f => f.IsAlive && f.CellId == c));
                me.CellId = from;
                me.CurrentAP = me.MaxAP;
                await FightHandler.AplicarEfectosAsync(null, fight, me, Step, 1, null, EffectEngine.AlLanzar, pink);
                _out.WriteLine($"step {from}->{pink}: me at {me.CellId}, lady hp {lady.CurrentHP}, alive {lady.IsAlive}");
                if (!lady.IsAlive) break;
                // One point a pink, not two: the rune goes off when she activates it, not also
                // when he lands on it.
                Assert.Equal(--life, lady.CurrentHP);
            }
            Assert.False(lady.IsAlive);
            // Her three Minikrons (5006) go with her: nobody is left on her side.
            Assert.DoesNotContain(fight.Todos, f => f.TeamId == lady.TeamId && f.IsAlive);
            Assert.True(me.IsAlive);
        }

        [Fact]
        public void The_quest_s_fights_are_set_as_the_guide_says()
        {
            // The tactical fight gives the Minikron its 12 AP.
            var tactical = NpcFights.For(3809, RoleplayMap, 32671);
            Assert.NotNull(tactical);
            Assert.Equal(Board, tactical!.ArenaMapId);
            Assert.Equal(Minikron, tactical.Incarnates);
            Assert.Equal(12, tactical.IncarnationActionPoints);

            // The Colère d'Ougah comes out of the mushroom at [-27,5] when it is clicked.
            Assert.Contains(NpcFights.OnMap(171966475), f => f.ElementId == 511296 && f.Monsters.Any(m => m.Monster == 4999));
        }
    }
}
