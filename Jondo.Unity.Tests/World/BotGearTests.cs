using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Bots;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The simulated players' equipment: what the planner puts on a bot of each level and element,
    /// and the ways to the marketplace counters and workshops they buy, sell and craft at.
    /// </summary>
    [Collection("MapManager")]
    public class BotGearTests
    {
        private static void World()
        {
            if (MapManager.GetMapInfo(CharacterCreationHandler.StartingMap) == null) MapManager.Initialize();
            if (Interactives.Waypoints.Count == 0) Interactives.Initialize();
            Workshops.Initialize();
            Marketplaces.Initialize();
            TeleportManager.Initialize();
            InteractiveRegistry.Initialize();
            if (RecipeManager.Count == 0) RecipeManager.Initialize();
        }

        private static Outfitter.Piece Piece(int gid, int type, int level, params (int Effect, int Value)[] lines)
            => new(gid, type, level, 0, false, 0,
                   lines.Select(l => new Forgemagic.TemplateLine(l.Effect, l.Value, l.Value, l.Value)).ToList());

        [Fact]
        public void A_bot_wears_its_elements_pieces_of_its_level_and_two_different_rings()
        {
            Outfitter.Declare(118, 10);      // strength
            Outfitter.Declare(126, 15);      // intelligence
            Outfitter.Declare(125, 11);      // vitality
            Outfitter.Declare(111, 1);       // AP
            var pool = new[]
            {
                Piece(900001, 1, 20, (118, 30)),
                Piece(900002, 1, 20, (126, 40)),
                Piece(900003, 1, 60, (118, 80)),
                Piece(900004, 9, 20, (118, 10)),
                Piece(900005, 9, 20, (118, 12)),
                Piece(900006, 9, 20, (125, 20)),
                Piece(900007, 16, 30, (111, 1)),
            };

            var plan = Outfitter.Best(40, Outfitter.Element.Strength, pool);

            Assert.Equal(900001, plan.Slots[Outfitter.Amulet].Gid);
            Assert.Equal(new[] { 900004, 900005 }, new[] { plan.Slots[Outfitter.LeftRing].Gid, plan.Slots[Outfitter.RightRing].Gid }.OrderBy(g => g));
            Assert.Equal(900007, plan.Slots[Outfitter.Hat].Gid);
            Assert.Equal(900002, Outfitter.Best(40, Outfitter.Element.Intelligence, pool).Slots[Outfitter.Amulet].Gid);
        }

        [Fact]
        public void A_two_handed_weapon_leaves_no_shield()
        {
            Outfitter.Declare(118, 10);
            var pool = new[]
            {
                Piece(900011, 82, 10, (118, 5)),
                Piece(900012, 6, 10, (118, 5)),
                new Outfitter.Piece(900013, 7, 10, 0, true, 0, new[] { new Forgemagic.TemplateLine(118, 30, 30, 30) }),
            };

            var plan = Outfitter.Best(10, Outfitter.Element.Strength, pool);

            Assert.Equal(900013, plan.Slots[Outfitter.Weapon].Gid);
            Assert.False(plan.Slots.ContainsKey(Outfitter.Shield));
        }

        [Fact]
        public void The_real_craftable_equipment_dresses_a_bot_of_every_level()
        {
            World();
            foreach (int level in new[] { 20, 100, 200 })
            {
                var plan = Outfitter.Best(level, Outfitter.Element.Agility, Outfitter.Craftable.Value);
                Assert.True(plan.Slots.Count >= 7, $"level {level}: {plan.Slots.Count} slots");
                Assert.All(plan.Slots, s =>
                {
                    Assert.True(s.Value.Level <= level);
                    Assert.Contains(s.Key, Outfitter.SlotsOf(s.Value.Type));
                    Assert.True(RecipeManager.TryGetByResult(s.Value.Gid, out _));
                });
                Assert.NotEqual(plan.Slots[Outfitter.LeftRing].Gid, plan.Slots[Outfitter.RightRing].Gid);
            }
        }

        [Fact]
        public void From_Astrub_the_counters_are_walked_to_and_a_workshop_is_through_its_door()
        {
            World();
            long start = CharacterCreationHandler.StartingMap;

            var toCounter = SimulatedPlayer.RouteTo(start, m => Marketplaces.On(m).Any(c => c.House.Id == SimulatedPlayer.EquipmentHouse));
            Assert.NotNull(toCounter);
            Assert.All(toCounter!, s => Assert.Equal(SimulatedPlayer.StepKind.Border, s.Kind));

            var toJeweller = SimulatedPlayer.RouteTo(start, m => m == 192937990);
            Assert.NotNull(toJeweller);
            Assert.Equal(SimulatedPlayer.StepKind.Passage, toJeweller!.Last().Kind);
        }

        [Fact]
        public void Far_from_Astrub_a_bot_takes_a_zaap()
        {
            World();
            var waypoint = Interactives.Waypoints.First(w => w.Activated && w.MapId != CharacterCreationHandler.StartingMap
                                                             && MapManager.GetMapInfo(w.MapId) != null
                                                             && SimulatedPlayer.AreaOf(w.MapId) != SimulatedPlayer.HomeArea.Value);

            var route = SimulatedPlayer.RouteTo(waypoint.MapId, m => m == CharacterCreationHandler.StartingMap);

            Assert.NotNull(route);
            Assert.Contains(route!, s => s.Kind == SimulatedPlayer.StepKind.Zaap);
        }

        [Fact]
        public void An_item_is_worth_ten_kamas_a_level_at_the_least()
        {
            World();
            var piece = Outfitter.Craftable.Value.First(p => p.Level >= 50);
            Assert.True(SimulatedPlayer.ValueOf(piece.Gid) >= 10L * piece.Level);
        }
    }
}
