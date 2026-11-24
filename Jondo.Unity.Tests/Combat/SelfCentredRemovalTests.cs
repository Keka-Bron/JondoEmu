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
    /// Spells cast on the caster's own cell that take AP or MP from the enemies round him: the Dark
    /// Miner's Desatino de Masas (8161, -2 AP in a circle of eight) and the Coralator's Golpe de
    /// Coral (1023, a blow and -1 MP in a circle of six). Weighed on the caster himself, the
    /// removal was worth nothing: the first was never cast and the second always lost to the
    /// Coralator's ranged spell.
    /// </summary>
    [Collection("MapManager")]
    public class SelfCentredRemovalTests
    {
        private readonly ITestOutputHelper _out;
        public SelfCentredRemovalTests(ITestOutputHelper output) => _out = output;

        [Theory]
        [InlineData(87295489L, 118, new[] { 234, 242, 8161, 8162 }, 8161, 4)]
        [InlineData(5243139L, 1022, new[] { 1023, 1024, 1025 }, 1023, 1)]
        public void The_monster_casts_its_removal_round_itself_when_an_enemy_stands_inside(
            long map, int template, int[] spells, int removal, int distance)
        {
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            if (!MapManager.WalkableCells.ContainsKey(map)) { _out.WriteLine("no world data"); return; }
            var fightCells = MapManager.GetFightWalkable(map);
            bool Free(int c) => MapGeometry.IsValid(c) && (fightCells?.Contains(c) ?? MapManager.IsCellWalkable(map, c));
            int playerCell = Enumerable.Range(0, MapGeometry.MaxCells).First(c => Free(c) && MapGeometry.GetNeighbors(c).Count(Free) >= 4
                && Enumerable.Range(0, MapGeometry.MaxCells).Any(b => Free(b) && MapGeometry.Distance(b, c) == distance));
            int monsterCell = Enumerable.Range(0, MapGeometry.MaxCells).First(b => Free(b) && MapGeometry.Distance(b, playerCell) == distance);

            var fight = new FightInstance(9_999_521, map, map);
            var monster = new Fighter
            {
                Id = -1, TeamId = 1, CellId = monsterCell, IsMonster = true, MonsterId = template, Level = 50,
                MaxHP = 500, CurrentHP = 500, MaxAP = 8, CurrentAP = 8, MaxMP = 4, CurrentMP = 4,
            };
            foreach (int spell in spells) { monster.SpellIds.Add(spell); monster.SpellGrades[spell] = 1; }
            var player = new Fighter
            {
                Id = 10, TeamId = 0, CellId = playerCell, MaxHP = 5000, CurrentHP = 5000, Level = 50,
                MaxAP = 7, CurrentAP = 7, MaxMP = 3, CurrentMP = 3,
            };
            fight.AddPlayer(player);
            fight.AddOpponent(monster);

            var usable = FightHandler.TacticsOf(monster).Where(s => SpellCriteria.Allows(monster, s.Id, s.Grade)).ToList();
            var plan = MonsterTactics.PlanTurn(FightHandler.BoardOf(fight), monster, usable);
            foreach (var step in plan) _out.WriteLine($"{step.Spell?.Id} at {step.TargetCell}");

            Assert.NotEmpty(plan);
            Assert.Equal(removal, plan[0].Spell.Id);
            Assert.Equal(monsterCell, plan[0].TargetCell);
        }
    }
}
