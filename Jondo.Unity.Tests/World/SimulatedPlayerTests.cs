using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Bots;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>The simulated players' ways about the world, read off the same data the server moves players with.</summary>
    [Collection("MapManager")]
    public class SimulatedPlayerTests
    {
        private static void Maps()
        {
            if (MapManager.GetMapInfo(CharacterCreationHandler.StartingMap) == null) MapManager.Initialize();
            if (Interactives.AreaOfSubArea(MapManager.GetMapInfo(CharacterCreationHandler.StartingMap).SubAreaId) < 0)
                Interactives.Initialize();
        }

        [Fact]
        public void The_start_map_has_ways_out_into_the_bots_area()
        {
            // The walkable cells leave the borders out, so that no monster stands on one; a bot that
            // only walked those never reached a border and stayed on the zaap's map for good.
            Maps();
            var ways = SimulatedPlayer.WaysOut(CharacterCreationHandler.StartingMap);
            int home = SimulatedPlayer.HomeArea.Value;
            Assert.True(home >= 0);
            Assert.True(ways.Count(w => SimulatedPlayer.AreaOf(w.To) == home) >= 2,
                $"home area {home}; ways: " + string.Join(", ",
                    ways.Select(w => $"{w.To} (area {SimulatedPlayer.AreaOf(w.To)}, {w.Cells.Length} cells)")));
            Assert.All(ways, w => Assert.True(MapManager.IsNeighbour(CharacterCreationHandler.StartingMap, w.To)));
        }

        [Fact]
        public void Three_bots_of_five_swear_to_a_city_and_both_cities_get_some()
        {
            var sides = Enumerable.Range(0, 20).Select(SimulatedPlayer.SideOf).ToList();
            Assert.Equal(12, sides.Count(s => s != Alignments.Neutral));
            Assert.Equal(6, sides.Count(s => s == Alignments.Bonta));
            Assert.Equal(6, sides.Count(s => s == Alignments.Brakmar));
        }

        [Fact]
        public void The_war_ground_is_the_next_area_where_aggressions_are_allowed()
        {
            // Astrub allows none: the bots at war go next door, where most maps do.
            Maps();
            int home = SimulatedPlayer.HomeArea.Value;
            int war = SimulatedPlayer.WarGround.Value;
            Assert.NotEqual(home, war);
            var maps = MapManager.Maps.Values.Where(m => Interactives.AreaOfSubArea(m.SubAreaId) == war).ToList();
            Assert.True(maps.Count(m => Alignments.MapAllowsAggression(m.MapId)) * 2 > maps.Count);
            Assert.False(Alignments.MapAllowsAggression(CharacterCreationHandler.StartingMap));
        }

        [Fact]
        public void A_path_travels_as_its_corners_each_facing_its_way()
        {
            // Five steps one way and three another: the start, the turn and the end, as the client
            // sends a walk, with the facing above the cell's twelve bits.
            int start = 300;
            var path = new List<int> { start };
            int Step(int from, int dx, int dy)
            {
                var (x, y) = MapGeometry.CellToPoint(from);
                return MapGeometry.PointToCell(x + dx, y + dy);
            }
            for (int i = 0; i < 5; i++) path.Add(Step(path[^1], 1, 0));
            int bend = path[^1];
            for (int i = 0; i < 3; i++) path.Add(Step(path[^1], 0, 1));
            Assert.All(path, c => Assert.True(c >= 0));
            for (int i = 1; i < path.Count; i++) Assert.Contains(path[i], MapGeometry.GetNeighbors(path[i - 1]));

            var corners = SimulatedPlayer.Corners(path);
            Assert.Equal(new[] { start, bend, path[^1] }, corners.Select(c => (int)(c & 0xFFF)));
            Assert.All(corners, c => Assert.True((c >> 12) % 2 == 1, "a step is a diagonal of the screen: an odd facing"));
            Assert.NotEqual(corners[1] >> 12, corners[2] >> 12);
        }
    }
}
