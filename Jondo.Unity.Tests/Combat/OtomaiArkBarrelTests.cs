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
    /// Otomai's Ark: Gourlo (1051) holds 200 resistance in every element until a barrel (1080) next
    /// to him casts Rhum Fourbe (938, 4 AP, -200 resistance around it for three turns). A barrel
    /// starts at 2 AP and casts 261 (+1 AP for six turns, once a turn); the guides have it go off
    /// on its third turn, so the +1 of 261 piles up although its level's maxStack is zero.
    /// </summary>
    [Collection("MapManager")]
    public class OtomaiArkBarrelTests
    {
        private const int Barrel = 1080, Gourlo = 1051, MoreAp = 261, RhumFourbe = 938;
        private const long Room = 107485184;

        private readonly ITestOutputHelper _out;
        public OtomaiArkBarrelTests(ITestOutputHelper output) => _out = output;

        private static Fighter NewBarrel(int cell) => new()
        {
            Id = -2, TeamId = 1, CellId = cell, IsMonster = true, MonsterId = Barrel, Level = 70,
            MaxHP = 190, CurrentHP = 190, MaxAP = 2, CurrentAP = 2, MaxMP = 0, CurrentMP = 0,
        };

        [Fact]
        public void A_barrel_s_ap_piles_up_turn_after_turn()
        {
            if (SpellEffects.De(MoreAp, 1).Count == 0) return;   // no spell data
            var fight = new FightInstance(1, 1);
            var barrel = NewBarrel(300);
            var player = new Fighter { Id = 10, TeamId = 0, CellId = 330, MaxHP = 1000, CurrentHP = 1000, MaxAP = 12, CurrentAP = 12, Level = 100 };
            fight.AddPlayer(player); fight.AddMonster(barrel);

            for (int turn = 0; turn < 2; turn++)
                EffectEngine.Resolver(fight, barrel, MoreAp, 1, barrel, EffectEngine.AlLanzar, fight.RoundNumber, celdaApuntada: barrel.CellId);

            Assert.Equal(2, barrel.Buffs.Puestos.Count(r => r.EffectId == 111 && r.HechizoOrigen == MoreAp));
        }

        [Fact]
        public void At_four_ap_the_barrel_casts_rhum_fourbe_next_to_gourlo()
        {
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            if (!MapManager.WalkableCells.ContainsKey(Room)) { _out.WriteLine("no world data"); return; }
            if (SpellEffects.De(RhumFourbe, 1).Count == 0) { _out.WriteLine("no spell data"); return; }

            var fightCells = MapManager.GetFightWalkable(Room);
            bool Free(int c) => MapGeometry.IsValid(c) && (fightCells?.Contains(c) ?? MapManager.IsCellWalkable(Room, c));
            var free = Enumerable.Range(0, MapGeometry.MaxCells).Where(Free).ToList();
            int barrelCell = free[free.Count / 2];
            int bossCell = free.First(c => MapGeometry.Distance(c, barrelCell) == 1);
            int playerCell = free.First(c => c != bossCell && MapGeometry.Distance(c, barrelCell) == 1);

            var fight = new FightInstance(9_999_152, Room, Room);
            var barrel = NewBarrel(barrelCell);
            barrel.MaxAP = 4; barrel.CurrentAP = 4;
            foreach (int spell in new[] { MoreAp, RhumFourbe })
            {
                barrel.SpellIds.Add(spell);
                barrel.SpellGrades[spell] = 1;
            }
            var boss = new Fighter
            {
                Id = -1, TeamId = 1, CellId = bossCell, IsMonster = true, MonsterId = Gourlo, Level = 70,
                MaxHP = 1900, CurrentHP = 1900, MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4,
            };
            var player = new Fighter
            {
                Id = 10, TeamId = 0, CellId = playerCell, MaxHP = 1000, CurrentHP = 1000, Level = 100,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            fight.AddPlayer(player); fight.AddOpponent(boss); fight.AddOpponent(barrel);

            var usable = FightHandler.TacticsOf(barrel).Where(s => SpellCriteria.Allows(barrel, s.Id, s.Grade)).ToList();
            _out.WriteLine("usable " + string.Join(",", usable.Select(s => s.Id)));
            var plan = MonsterTactics.PlanTurn(FightHandler.BoardOf(fight), barrel, usable, trace: _out.WriteLine);
            foreach (var step in plan) _out.WriteLine($"plan: {step.Spell?.Id} at {step.TargetCell} score {step.Score}");

            Assert.Contains(plan, a => a.Spell?.Id == RhumFourbe);
        }
    }
}
