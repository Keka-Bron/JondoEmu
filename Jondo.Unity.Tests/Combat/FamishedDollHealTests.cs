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
    /// The Famished Doll (676) in Skeunk's Hideout: every turn she gives her whole side back
    /// 1,250 life with 712 («108 a», a circle of 63 around her own cell, 1 AP, range 0).
    /// With the boss at a third of his life, she only ever walked.
    /// </summary>
    [Collection("MapManager")]
    public class FamishedDollHealTests
    {
        private const long BossRoom = 107485184;
        private const int Doll = 676, Skeunk = 780, Feast = 712;

        private readonly ITestOutputHelper _out;
        public FamishedDollHealTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public void She_heals_her_wounded_side()
        {
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            if (!MapManager.WalkableCells.ContainsKey(BossRoom)) { _out.WriteLine("no world data"); return; }

            var fightCells = MapManager.GetFightWalkable(BossRoom);
            bool Free(int c) => MapGeometry.IsValid(c) && (fightCells?.Contains(c) ?? MapManager.IsCellWalkable(BossRoom, c));
            var free = Enumerable.Range(0, MapGeometry.MaxCells).Where(Free).ToList();
            int dollCell = free[free.Count / 2];
            int bossCell = free.First(c => MapGeometry.Distance(c, dollCell) == 3);
            int playerCell = free.First(c => MapGeometry.Distance(c, dollCell) >= 8 && MapGeometry.Distance(c, bossCell) >= 8);

            var fight = new FightInstance(9_999_151, BossRoom, BossRoom);
            var doll = new Fighter
            {
                Id = -2, TeamId = 1, CellId = dollCell, IsMonster = true, MonsterId = Doll, Level = 120,
                MaxHP = 1500, CurrentHP = 1500, MaxAP = 4, CurrentAP = 4, MaxMP = 3, CurrentMP = 3,
            };
            foreach (int spell in new[] { 710, 711, Feast })
            {
                doll.SpellIds.Add(spell);
                doll.SpellGrades[spell] = 1;
            }
            var boss = new Fighter
            {
                Id = -1, TeamId = 1, CellId = bossCell, IsMonster = true, MonsterId = Skeunk, Level = 120,
                MaxHP = 6000, CurrentHP = 2000, MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4,
            };
            var player = new Fighter
            {
                Id = 10, TeamId = 0, CellId = playerCell, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            fight.AddPlayer(player); fight.AddOpponent(boss); fight.AddOpponent(doll);

            var usable = FightHandler.TacticsOf(doll).Where(s => SpellCriteria.Allows(doll, s.Id, s.Grade)).ToList();
            _out.WriteLine("usable " + string.Join(",", usable.Select(s => s.Id)));
            if (usable.All(s => s.Id != Feast)) { _out.WriteLine("no spell data"); return; }

            var plan = MonsterTactics.PlanTurn(FightHandler.BoardOf(fight), doll, usable, trace: _out.WriteLine);
            foreach (var step in plan) _out.WriteLine($"plan: {step.Spell.Id} at {step.TargetCell} score {step.Score}");

            Assert.Contains(plan, a => a.Spell.Id == Feast);
        }
    }
}
