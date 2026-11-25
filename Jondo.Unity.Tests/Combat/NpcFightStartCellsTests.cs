using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Where a quest's fight starts its fighters (content/fights/npc_fights.json «playerCell»,
    /// «allyCells», a monster's «cell»): «Le jour des assassins» is won by putting the magypus to
    /// sleep in the first round at range 1, and a drawn placement left the character rows away.
    /// </summary>
    public class NpcFightStartCellsTests
    {
        private static Fighter Monster(long id, int template, int cell)
            => new() { Id = id, TeamId = 1, CellId = cell, MaxHP = 500, CurrentHP = 500, Level = 40,
                       IsMonster = true, MonsterId = template };

        private static Fighter Player(int cell)
            => new() { Id = 1, TeamId = 0, CellId = cell, MaxHP = 1000, CurrentHP = 1000, Level = 60 };

        [Fact]
        public void A_row_reads_its_start_cells_and_an_old_row_has_none()
        {
            using var row = JsonDocument.Parse("""
                { "npc": 1, "reply": 2, "playerCell": 300, "allyCells": [ 0, 314 ],
                  "allies": [ { "monster": 10, "grade": 0 }, { "monster": 11, "grade": 0 } ],
                  "monsters": [ { "monster": 5174, "grade": 0 }, { "monster": 5177, "grade": 0, "cell": 301 } ] }
                """);
            var fight = NpcFights.Parse(row.RootElement);
            Assert.Equal(new[] { (5174, 0), (5177, 0) }, fight.Monsters);
            var start = Assert.IsType<NpcFights.StartCells>(fight.Start);
            Assert.Equal(300, start.Player);
            Assert.Equal(new[] { 0, 314 }, start.Allies);
            Assert.Equal(new[] { 0, 301 }, start.Monsters);

            using var plain = JsonDocument.Parse("""{ "npc": 1, "reply": 2, "monsters": [ { "monster": 5174, "grade": 0 } ] }""");
            Assert.Null(NpcFights.Parse(plain.RootElement).Start);
        }

        [Fact]
        public void Fixed_cells_win_over_the_board_s_placement()
        {
            var fight = new FightInstance(1, 1);
            fight.SetPlacementCells(new[] { 100, 101, 102 }, new[] { 400, 401, 402 });
            var player = Player(100);
            fight.AddPlayer(player);
            var assassin = Monster(-1, 5174, 400);
            var magypus = Monster(-2, 5177, 401);
            fight.AddMonster(assassin);
            fight.AddMonster(magypus);

            var walkable = new HashSet<int> { 100, 101, 102, 300, 301, 330, 400, 401, 402 };
            FightHandler.PinStartCells(fight, player, new List<Fighter>(), new[] { assassin, magypus },
                new NpcFights.StartCells(300, new List<int>(), new[] { 330, 301 }), walkable);

            Assert.Equal(300, player.CellId);
            Assert.Equal(330, assassin.CellId);
            Assert.Equal(301, magypus.CellId);
            Assert.Contains(300, fight.BluePlacementCells);
            Assert.Contains(301, fight.RedPlacementCells);

            // A cell the board cannot be stood on is left to the board.
            var other = Player(100);
            var fight2 = new FightInstance(2, 1);
            fight2.SetPlacementCells(new[] { 100 }, new[] { 400 });
            fight2.AddPlayer(other);
            FightHandler.PinStartCells(fight2, other, new List<Fighter>(), new List<Fighter>(),
                new NpcFights.StartCells(555, new List<int>(), new List<int>()), walkable);
            Assert.Equal(100, other.CellId);
        }

        [Fact]
        public void A_board_with_no_walkable_placement_is_placed_on_its_own_cells()
        {
            var walkable = new HashSet<int>(Enumerable.Range(200, 12));
            // The fixed list a board under sixteen cells used to get: none of it on this board.
            var usable = FightHandler.UsablePlacement(new[] { 286, 298 }, new[] { 411, 424 }, walkable);
            var (blue, red) = Assert.IsType<(List<int>, List<int>)>(usable.Value);
            Assert.Equal(6, blue.Count);
            Assert.Equal(6, red.Count);
            Assert.All(blue.Concat(red), c => Assert.Contains(c, walkable));
            Assert.Empty(blue.Intersect(red));

            // A placement that is usable stays as it is, and an unknown board too.
            Assert.Null(FightHandler.UsablePlacement(new[] { 200 }, new[] { 211 }, walkable));
            Assert.Null(FightHandler.UsablePlacement(new[] { 286 }, new[] { 411 }, null));
        }

        [Fact]
        public void The_assassins_fights_start_the_character_beside_the_magypus()
        {
            string fights = Path.Combine(Paths.ContentDir, "fights", "npc_fights.json");
            if (!File.Exists(fights) || !File.Exists(Paths.FightCellsJson)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(fights));
            using var boards = JsonDocument.Parse(File.ReadAllText(Paths.FightCellsJson));
            var rows = doc.RootElement.GetProperty("fights").EnumerateArray()
                .Where(r => r.TryGetProperty("reply", out var reply) && reply.GetInt32() == 35966).ToList();
            Assert.Equal(19, rows.Count);
            foreach (var row in rows)
            {
                var fight = NpcFights.Parse(row);
                var start = Assert.IsType<NpcFights.StartCells>(fight.Start);
                Assert.True(boards.RootElement.TryGetProperty(fight.ArenaMapId.ToString(), out var board),
                            $"arena {fight.ArenaMapId} has no fight cells");
                var walkable = board.GetProperty("f").EnumerateArray().Select(c => c.GetInt32()).ToHashSet();
                int magypus = start.Monsters[fight.Monsters.ToList().FindIndex(m => m.Item1 == 5177)];
                int assassin = start.Monsters[fight.Monsters.ToList().FindIndex(m => m.Item1 == 5174)];
                Assert.All(new[] { start.Player, magypus, assassin }, c => Assert.Contains(c, walkable));
                Assert.Equal(1, MapGeometry.Distance(start.Player, magypus));
                Assert.Equal(3, MapGeometry.Distance(start.Player, assassin));
            }
        }
    }
}
