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
    /// The Puñeta (2951) the Obsidemon summons has one spell, Piquete (2601): born waiting four
    /// turns, it then hurts the enemies eight cells or more away and kills the Puñeta. Weighed by
    /// the enemies it reached it was worth nothing with the player beside it, and the Puñeta stood
    /// there for the rest of the fight.
    /// </summary>
    [Collection("MapManager")]
    public class PunetaBlowUpTests
    {
        private const long Room = 234618880;
        private const int Puneta = 2951, Piquete = 2601;

        private readonly ITestOutputHelper _out;
        public PunetaBlowUpTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public void Off_its_wait_it_blows_up_with_the_player_beside_it()
        {
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            if (!MapManager.WalkableCells.ContainsKey(Room)) { _out.WriteLine("no world data"); return; }

            bool Free(int c) => MapGeometry.IsValid(c) && MapManager.IsCellWalkable(Room, c);
            int punetaCell = Enumerable.Range(0, MapGeometry.MaxCells).First(c => Free(c) && MapGeometry.GetNeighbors(c).Count(Free) >= 3);
            int playerCell = MapGeometry.GetNeighbors(punetaCell).First(Free);

            var fight = new FightInstance(9_999_151, Room, Room);
            var player = new Fighter
            {
                Id = 10, TeamId = 0, CellId = playerCell, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            var puneta = new Fighter
            {
                Id = -2, TeamId = 1, CellId = punetaCell, IsMonster = true, MonsterId = Puneta, Level = 160,
                MaxHP = 800, CurrentHP = 800, MaxAP = 6, CurrentAP = 6, MaxMP = 0, CurrentMP = 0, Invocador = -1,
            };
            puneta.SpellIds.Add(Piquete);
            puneta.SpellGrades[Piquete] = 1;
            fight.AddPlayer(player); fight.AddOpponent(puneta);

            var usable = FightHandler.TacticsOf(puneta).Where(s => SpellCriteria.Allows(puneta, s.Id, s.Grade)).ToList();
            if (usable.All(s => s.Id != Piquete)) { _out.WriteLine("no spell data"); return; }
            Assert.True(usable.First(s => s.Id == Piquete).BlowsUpOnTimer);

            var plan = MonsterTactics.PlanTurn(FightHandler.BoardOf(fight), puneta, usable, trace: _out.WriteLine);
            Assert.Contains(plan, a => a.Spell.Id == Piquete);
        }
    }
}
