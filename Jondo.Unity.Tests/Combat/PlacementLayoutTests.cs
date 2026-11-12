using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The placement is drawn anew for each fight: the same sixteen cells came out on almost every
    /// map, fight after fight. See <see cref="PlacementLayout"/>.
    /// </summary>
    [Collection("MapManager")]
    public class PlacementLayoutTests
    {
        /// <summary>A whole map, every one of its 560 cells walkable.</summary>
        private static List<int> OpenMap() => Enumerable.Range(0, 560).ToList();

        [Fact]
        public void Two_teams_of_eight_apart_and_never_touching()
        {
            var dice = new Random(7);
            for (int i = 0; i < 50; i++)
            {
                var (blue, red) = PlacementLayout.Draw(OpenMap(), dice)!.Value;

                Assert.Equal(8, blue.Distinct().Count());
                Assert.Equal(8, red.Distinct().Count());
                Assert.Empty(blue.Intersect(red));
                Assert.All(red, r => Assert.All(blue, b => Assert.True(MapGeometry.Distance(b, r) > 1)));
            }
        }

        /// <summary>With room enough, a team is spread out: no two of it side by side.</summary>
        [Fact]
        public void On_open_ground_teammates_do_not_stand_side_by_side()
        {
            var dice = new Random(5);
            for (int i = 0; i < 50; i++)
            {
                var (blue, red) = PlacementLayout.Draw(OpenMap(), dice)!.Value;
                foreach (var team in new[] { blue, red })
                    foreach (int a in team)
                        Assert.All(team.Where(b => b != a), b => Assert.True(MapGeometry.Distance(a, b) > 1));
            }
        }

        [Fact]
        public void It_changes_from_one_fight_to_the_next()
        {
            var fight = new FightInstance(1, 1, 1);
            var seen = new HashSet<string>();
            var dice = new Random(11);
            for (int i = 0; i < 20; i++)
            {
                fight.GeneratePlacementCells(OpenMap(), dice);
                seen.Add(string.Join(",", fight.BluePlacementCells) + "|" + string.Join(",", fight.RedPlacementCells));
            }
            Assert.True(seen.Count > 15, $"only {seen.Count} different placements in 20 fights");
        }

        [Fact]
        public void Without_dice_it_is_the_same_every_time()
        {
            // A dream's room tells beforehand where its monsters will stand.
            var a = new FightInstance(1, 1, 1);
            var b = new FightInstance(2, 1, 1);
            a.GeneratePlacementCells(OpenMap(), null!);
            b.GeneratePlacementCells(OpenMap());
            Assert.Equal(a.BluePlacementCells, b.BluePlacementCells);
            Assert.Equal(a.RedPlacementCells, b.RedPlacementCells);
        }

        [Fact]
        public void Nobody_is_placed_in_a_pocket_cut_off_from_the_rest()
        {
            // The map's right side and, far from it, a little island of cells nothing walkable joins.
            var ground = OpenMap().Where(c => c % 14 >= 7 && c < 400).ToList();
            var island = new[] { 500, 501, 514, 515 };
            Assert.All(island, c => Assert.All(ground, g => Assert.True(MapGeometry.Distance(c, g) > 1)));
            var walkable = ground.Concat(island).ToList();

            var dice = new Random(3);
            for (int i = 0; i < 50; i++)
            {
                var (blue, red) = PlacementLayout.Draw(walkable, dice)!.Value;
                Assert.Empty(blue.Concat(red).Intersect(island));
            }
        }

        /// <summary>
        /// The preview (L, the kaz) shows the cells the next fight really takes, and after that
        /// fight there is another: it showed the fixed placement while the fight drew its own.
        /// </summary>
        [Fact]
        public void The_preview_is_the_next_fight_s_placement_and_changes_after_it()
        {
            const long astrub = 188744196;
            if (!MapManager.WalkableCells.ContainsKey(astrub)) MapManager.Initialize();
            long arena = MapManager.ResolveArenaMapId(astrub);
            if (FightHandler.PlacementGround(arena).Count < FightInstance.PlacesForBothTeams) return;   // no world data

            var seen = new HashSet<string>();
            for (int i = 0; i < 10; i++)
            {
                var shown = FightHandler.Placement(astrub);
                var again = FightHandler.Placement(astrub);   // asked twice, the same
                Assert.Equal(shown.Attackers, again.Attackers);
                Assert.Equal(shown.Defenders, again.Defenders);

                var taken = FightHandler.TakePlacement(astrub, arena);
                Assert.Equal(shown.Attackers, taken.Attackers);
                Assert.Equal(shown.Defenders, taken.Defenders);
                seen.Add(string.Join(",", taken.Attackers) + "|" + string.Join(",", taken.Defenders));
            }
            Assert.True(seen.Count > 5, $"only {seen.Count} different placements in 10 fights");
        }

        [Fact]
        public void Too_little_ground_is_left_to_the_fixed_placement()
        {
            Assert.Null(PlacementLayout.Draw(Enumerable.Range(0, 10).ToList(), new Random(1)));

            var fight = new FightInstance(1, 1, 1);
            fight.GeneratePlacementCells(Enumerable.Range(200, 10).ToList(), new Random(1));
            Assert.Equal(8, fight.BluePlacementCells.Count);
            Assert.Equal(8, fight.RedPlacementCells.Count);
        }
    }
}
