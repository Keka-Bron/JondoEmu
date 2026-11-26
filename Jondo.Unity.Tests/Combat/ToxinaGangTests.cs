using System.Collections.Generic;
using System.Linq;
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
    /// Toxina (5187) in the Azrim cave, fought alone with the Queen of Thieves (5188) at the
    /// character's side (quest 1937). Toxina's «Rassemblement du gang» summons a poison hunter
    /// (5156) -- «invoque une Chasseuse de poison de Toxine» -- and the Queen's «Consolation des
    /// ombres» heals her or an ally (Dofus pour les noobs, «Le piège se referme»).
    /// </summary>
    [Collection("MapManager")]
    public class ToxinaGangTests
    {
        private const long Cave = 171707400;
        private const int Toxina = 5187, Queen = 5188;
        private const int GangHigh = 10115, GangLow = 10116, Consolation = 10157;

        private readonly ITestOutputHelper _out;
        public ToxinaGangTests(ITestOutputHelper output) => _out = output;

        private static (FightInstance Fight, Fighter Toxina, Fighter Queen, Fighter Player)? Build(int gradeIndex, int distance)
        {
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            if (!MapManager.WalkableCells.ContainsKey(Cave)) return null;
            MobSpawnManager.EnsureMonsterData();
            var toxinaData = MobSpawnManager.GetMonsterData(Toxina);
            var queenData = MobSpawnManager.GetMonsterData(Queen);
            if (toxinaData == null || queenData == null) return null;

            var fightCells = MapManager.GetFightWalkable(Cave);
            bool Free(int c) => MapGeometry.IsValid(c) && (fightCells?.Contains(c) ?? MapManager.IsCellWalkable(Cave, c));
            int playerCell = Enumerable.Range(0, MapGeometry.MaxCells)
                .First(c => Free(c) && MapGeometry.GetNeighbors(c).Count(Free) >= 3
                            && Enumerable.Range(0, MapGeometry.MaxCells).Any(b => Free(b) && MapGeometry.Distance(b, c) == distance));
            int toxinaCell = Enumerable.Range(0, MapGeometry.MaxCells)
                .First(b => Free(b) && MapGeometry.Distance(b, playerCell) == distance && MapGeometry.GetNeighbors(b).Count(Free) >= 3);
            int queenCell = MapGeometry.GetNeighbors(playerCell).First(Free);

            var fight = new FightInstance(9_951_870, Cave, Cave);
            var player = new Fighter
            {
                Id = 10, TeamId = 0, CellId = playerCell, MaxHP = 4000, CurrentHP = 4000, Level = 200,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            var toxina = FightHandler.BuildMonsterFighter(new MobSpawnManager.MobMember { Monster = toxinaData, GradeIndex = gradeIndex }, -1, toxinaCell);
            toxina.TeamId = 1;
            toxina.CellId = toxinaCell;
            var queen = FightHandler.BuildMonsterFighter(new MobSpawnManager.MobMember { Monster = queenData, GradeIndex = gradeIndex }, -2, queenCell);
            queen.TeamId = 0;
            queen.CellId = queenCell;
            fight.AddPlayer(player);
            fight.AddPlayer(queen);
            fight.AddOpponent(toxina);
            return (fight, toxina, queen, player);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(8)]
        public void Toxina_gathers_her_gang(int gradeIndex)
        {
            var built = Build(gradeIndex, 6);
            if (built == null) { _out.WriteLine("no world data"); return; }
            var (fight, toxina, _, _) = built.Value;
            _out.WriteLine("spells: " + string.Join(", ", toxina.SpellIds.Select(s => $"{s}/{toxina.SpellGrades.GetValueOrDefault(s)}"))
                           + " waiting: " + string.Join(", ", toxina.Recarga.Select(kv => $"{kv.Key}:{kv.Value}")));

            var spells = FightHandler.TacticsOf(toxina);
            var usable = spells.Where(s => SpellCriteria.Allows(toxina, s.Id, s.Grade)).ToList();
            foreach (var s in spells)
                _out.WriteLine($"spell {s.Id}/{s.Grade} cost {s.Cost} summons {s.Summons} offensive {s.Offensive} utility {s.Utility} usable {usable.Contains(s)}");

            // Only one of the two is hers at a grade: 10116 up to level 80, 10115 from 100.
            int gang = gradeIndex <= 2 ? GangLow : GangHigh;
            int other = gang == GangLow ? GangHigh : GangLow;
            Assert.Contains(usable, s => s.Id == gang);
            Assert.DoesNotContain(usable, s => s.Id == other);

            var board = FightHandler.BoardOf(fight);
            foreach (var option in MonsterTactics.Options(board, toxina, usable).OrderByDescending(o => o.Score).Take(12))
                _out.WriteLine($"option {option.Spell.Id} at {option.TargetCell} from {option.From} score {option.Score:0.0}");

            // Her turn as the fight plays it: the best thing at each step while AP last.
            var cast = new List<int>();
            for (int step = 0; step < 6; step++)
            {
                var action = MonsterTactics.Next(board, toxina, usable);
                if (action == null) break;
                _out.WriteLine($"step {step}: {action.Spell.Id} at {action.TargetCell} score {action.Score:0.0}");
                cast.Add(action.Spell.Id);
                toxina.CurrentAP -= action.Spell.Cost;
                toxina.CellId = action.From;
                toxina.LanzadosEsteTurno[action.Spell.Id] = toxina.LanzadosEsteTurno.GetValueOrDefault(action.Spell.Id) + 1;
            }
            Assert.Contains(gang, cast);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_Queen_consoles_the_wounded(bool herself)
        {
            var built = Build(8, 3);
            if (built == null) { _out.WriteLine("no world data"); return; }
            var (fight, _, queen, player) = built.Value;
            if (herself) queen.CurrentHP = queen.MaxHP / 3;
            else player.CurrentHP = player.MaxHP / 3;

            var spells = FightHandler.TacticsOf(queen);
            var usable = spells.Where(s => SpellCriteria.Allows(queen, s.Id, s.Grade)).ToList();
            foreach (var s in spells)
                _out.WriteLine($"spell {s.Id}/{s.Grade} cost {s.Cost} heal {s.Heal} offensive {s.Offensive} utility {s.Utility} usable {usable.Contains(s)}");
            Assert.Contains(usable, s => s.Id == Consolation);

            var board = FightHandler.BoardOf(fight);
            foreach (var option in MonsterTactics.Options(board, queen, usable).OrderByDescending(o => o.Score).Take(12))
                _out.WriteLine($"option {option.Spell.Id} at {option.TargetCell} target {option.Target.Id} score {option.Score:0.0}");
            var cast = new List<int>();
            for (int step = 0; step < 6; step++)
            {
                var action = MonsterTactics.Next(board, queen, usable);
                if (action == null) break;
                _out.WriteLine($"step {step}: {action.Spell.Id} at {action.TargetCell} score {action.Score:0.0}");
                cast.Add(action.Spell.Id);
                queen.CurrentAP -= action.Spell.Cost;
                queen.CellId = action.From;
                queen.LanzadosEsteTurno[action.Spell.Id] = queen.LanzadosEsteTurno.GetValueOrDefault(action.Spell.Id) + 1;
            }
            Assert.Contains(Consolation, cast);
        }

        /// <summary>
        /// Otomai's toxin immunity potion (19507), held into the fight, leaves Toxinistrose (10170)
        /// nothing to poison: its description (745991) and Otomai's line (743111). Without it the
        /// poison goes on to tick.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_potion_keeps_her_poison_off(bool holdsThePotion)
        {
            var built = Build(8, 1);
            if (built == null) { _out.WriteLine("no world data"); return; }
            var (fight, toxina, _, player) = built.Value;

            using var row = System.Text.Json.JsonDocument.Parse(
                "{\"antidotes\": [{\"item\": 19507, \"spells\": [10170, 8885]}]}");
            var rules = NpcFights.RulesOf(row.RootElement);
            Assert.False(rules.IsEmpty);
            FightHandler.ApplyRules(fight, player, rules, held: item => holdsThePotion && item == 19507 ? 1 : 0);
            Assert.Equal(holdsThePotion, player.SpellImmunities.Contains(10170));

            var outcomes = EffectEngine.Resolver(fight, toxina, 10170, 1, player, EffectEngine.AlLanzar, fight.RoundNumber,
                                                 celdaApuntada: player.CellId);
            _out.WriteLine($"outcomes on the player {outcomes.Count(o => o.Sobre == player)}, buffs "
                           + string.Join(", ", player.Buffs.Puestos.Select(b => $"{b.EffectId}/{b.HechizoOrigen}")));
            if (holdsThePotion)
            {
                Assert.DoesNotContain(outcomes, o => o.Sobre == player);
                Assert.DoesNotContain(player.Buffs.Puestos, b => b.HechizoOrigen == 10170);
            }
            // Without it the poison is armed on the player, to tick at the end of each of his turns.
            else Assert.Contains(outcomes, o => o.Sobre == player && o.HechizoOrigen == 10170 && o.Efecto.EffectId == 98);
        }
    }
}
