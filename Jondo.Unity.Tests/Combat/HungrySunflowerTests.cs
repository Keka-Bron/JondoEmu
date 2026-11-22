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
    /// The expedition's Hungry Sunflower (7796): no blow reaches him from afar and nothing moves
    /// him, and he walks two cells. He goes to the enemy another way: Ecodigestion (28834) swaps
    /// him with one of his own -- «il peut se téléporter vers un allié ou échanger sa place avec
    /// lui» (Dofus pour les noobs, expeditions season 2) -- and Atiborramiento (28837) then eats
    /// whoever stands at his contact.
    /// </summary>
    [Collection("MapManager")]
    public class HungrySunflowerTests
    {
        private const long BossRoom = 239608836;
        private const int Ecodigestion = 28834, Atiborramiento = 28837;
        private const int Unmovable = 97, FarOff = 375, Ripe = 5231;

        private readonly ITestOutputHelper _out;
        public HungrySunflowerTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public void He_swaps_with_his_summon_beside_the_enemy_and_eats_him()
        {
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            if (!MapManager.WalkableCells.ContainsKey(BossRoom)) { _out.WriteLine("no world data"); return; }

            var fightCells = MapManager.GetFightWalkable(BossRoom);
            bool Free(int c) => MapGeometry.IsValid(c) && (fightCells?.Contains(c) ?? MapManager.IsCellWalkable(BossRoom, c));
            int playerCell = Enumerable.Range(0, MapGeometry.MaxCells)
                .First(c => Free(c) && MapGeometry.GetNeighbors(c).Count(Free) >= 3
                            && Enumerable.Range(0, MapGeometry.MaxCells).Any(b => Free(b) && MapGeometry.Distance(b, c) == 8));
            int summonCell = MapGeometry.GetNeighbors(playerCell).First(Free);
            int bossCell = Enumerable.Range(0, MapGeometry.MaxCells)
                .First(b => Free(b) && MapGeometry.Distance(b, playerCell) == 8 && MapGeometry.Distance(b, summonCell) >= 7);

            var fight = new FightInstance(9_999_150, BossRoom, BossRoom);
            var boss = new Fighter
            {
                Id = -1, TeamId = 1, CellId = bossCell, IsMonster = true, MonsterId = 7796, Level = 220,
                MaxHP = 13000, CurrentHP = 13000, MaxAP = 12, CurrentAP = 12, MaxMP = 2, CurrentMP = 2,
            };
            foreach (int spell in new[] { 28834, 28835, 28836, 28837 })
            {
                boss.SpellIds.Add(spell);
                boss.SpellGrades[spell] = 1;
            }
            var summon = new Fighter
            {
                Id = -2, TeamId = 1, CellId = summonCell, IsMonster = true, MonsterId = 7799, Level = 200,
                MaxHP = 2000, CurrentHP = 2000, MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, Invocador = -1,
            };
            var player = new Fighter
            {
                Id = 10, TeamId = 0, CellId = playerCell, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            fight.AddPlayer(player); fight.AddOpponent(boss); fight.AddOpponent(summon);
            boss.Buffs.PonerEstado(Unmovable);
            boss.Buffs.PonerEstado(FarOff);
            summon.Buffs.PonerEstado(Ripe);

            var usable = FightHandler.TacticsOf(boss).Where(s => SpellCriteria.Allows(boss, s.Id, s.Grade)).ToList();
            if (usable.All(s => s.Id != Ecodigestion)) { _out.WriteLine("no spell data " + string.Join(",", usable.Select(s => s.Id))); return; }

            // Weighed by its rows: the summary read the swap as a mechanic on himself.
            Assert.True(usable.First(s => s.Id == Ecodigestion).HasRows);

            var plan = MonsterTactics.PlanTurn(FightHandler.BoardOf(fight), boss, usable, trace: _out.WriteLine);
            foreach (var step in plan) _out.WriteLine($"plan: {step.Spell.Id} at {step.TargetCell} score {step.Score}");

            var swap = plan.FirstOrDefault(a => a.Spell.Id == Ecodigestion);
            Assert.NotNull(swap);
            Assert.Equal(summonCell, swap!.TargetCell);
            Assert.Contains(plan.SkipWhile(a => a != swap), a => a.Spell.Id == Atiborramiento && a.TargetCell == playerCell);
        }
    }
}
