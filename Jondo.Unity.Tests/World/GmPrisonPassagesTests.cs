using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The GM prison's three maps made one round: the sky jail's trapdoor down to the dungeon,
    /// the dungeon's hanging cage up to the island, the island's chest back to the jail.
    /// </summary>
    /// <remarks>
    /// Each end is what the client's map data has (bundle world_401): the trapdoor 479454 is the
    /// jail's one stair, the chest 479462 the island's one element, and the dungeon has none, so
    /// its way out is a floor passage. See content/interactives/teleports.json and
    /// floor_passages.json.
    /// </remarks>
    [Collection("MapManager")]
    public class GmPrisonPassagesTests
    {
        private const long Sky = Jail.MapId, Dungeon = 105120002, Island = 105119744;
        private const int Trapdoor = 479454, TrapdoorCell = 314, Chest = 479462, Cage = 333;

        private static bool WorldLoaded()
        {
            if (!MapManager.WalkableCells.ContainsKey(Sky)) MapManager.Initialize();
            if (MapManager.GetMapInfo(Sky) == null || MapManager.GetMapInfo(Dungeon) == null ||
                MapManager.GetMapInfo(Island) == null) return false;
            Interactives.Initialize();
            TeleportManager.Initialize();
            return true;
        }

        /// <summary>The cells reachable on foot from <paramref name="from"/>, diagonals counted.</summary>
        private static HashSet<int> Reachable(long mapId, int from)
        {
            var floor = new HashSet<int>(MapManager.WalkableCells[mapId]);
            var seen = new HashSet<int>();
            var todo = new Stack<int>();
            todo.Push(from);
            while (todo.Count > 0)
            {
                int cell = todo.Pop();
                if (!floor.Contains(cell) || !seen.Add(cell)) continue;
                var at = MapGeometry.CellToPoint(cell);
                foreach (int other in floor)
                {
                    var there = MapGeometry.CellToPoint(other);
                    if (Math.Abs(there.X - at.X) <= 1 && Math.Abs(there.Y - at.Y) <= 1) todo.Push(other);
                }
            }
            return seen;
        }

        private static bool NextTo(HashSet<int> cells, int element)
            => cells.Any(c => MapGeometry.Distance(c, element) == 1);

        [Fact]
        public void The_trapdoor_goes_down_to_the_dungeon_room_with_the_cage()
        {
            if (!WorldLoaded()) return;   // no world data

            Assert.True(TeleportManager.TryGet(Sky, Trapdoor, out var down));
            Assert.Equal((TrapdoorCell, Dungeon, 370), (down.SourceCellId, down.DestinationMapId, down.DestinationCellId));
            Assert.Equal((-1, 184), (down.InteractiveType, down.SkillId));

            // Clicked from the corridor, and landing where the cage can be walked to: the 40
            // cells behind the dungeon's bars are walled off from its big room.
            var corridor = Jail.LayoutOf(MapManager.WalkableCells[Sky])!.Corridor;
            Assert.True(NextTo(new HashSet<int>(corridor), TrapdoorCell));
            Assert.Contains(Cage, Reachable(Dungeon, down.DestinationCellId));
        }

        [Fact]
        public void The_cage_lifts_to_the_island_and_is_not_declared_to_the_client()
        {
            if (!WorldLoaded()) return;

            Assert.True(TeleportManager.TryGetCellTrigger(Dungeon, Cage, out var up));
            Assert.Equal((Island, 342), (up.DestinationMapId, up.DestinationCellId));
            // A floor passage has no element: nothing on the dungeon is announced as clickable.
            Assert.Empty(TeleportManager.On(Dungeon));
            Assert.Contains(up.DestinationCellId, MapManager.WalkableCells[Island]);
        }

        [Fact]
        public void The_chest_brings_back_to_the_jail_corridor_at_the_foot_of_the_trapdoor()
        {
            if (!WorldLoaded()) return;

            Assert.True(TeleportManager.TryGet(Island, Chest, out var back));
            Assert.Equal((300, Sky, 342), (back.SourceCellId, back.DestinationMapId, back.DestinationCellId));
            Assert.Equal((-1, 184), (back.InteractiveType, back.SkillId));

            // The chest can be walked up to from where the cage lands, and the jail end is the
            // corridor -- not one of the four cells, which are walled off from it.
            Assert.True(NextTo(Reachable(Island, 342), back.SourceCellId));
            var corridor = Jail.LayoutOf(MapManager.WalkableCells[Sky])!.Corridor;
            Assert.Contains(back.DestinationCellId, corridor);
            Assert.True(MapGeometry.Distance(back.DestinationCellId, TrapdoorCell) <= 2);
        }

        /// <summary>
        /// The island's chest is declared to the client as the door it now is, and not as the
        /// olivioleta tree its placeholder graphic 682 stands for everywhere else -- as a tree it
        /// asked for woodcutting 90, and declared both ways the start stopped.
        /// </summary>
        [Fact]
        public void The_chest_is_declared_as_a_door_and_not_as_a_tree()
        {
            if (!WorldLoaded()) return;
            Resources.Initialize();
            InteractiveRegistry.Initialize();

            Assert.False(Resources.Is(Island, Chest));
            var chest = Assert.Single(InteractiveRegistry.OnMap(Island), i => i.Element.Id == Chest);
            Assert.Equal(-1, chest.Type);
            var open = Assert.Single(chest.Actions);
            Assert.Equal((InteractiveActionKind.Teleport, 184), (open.Kind, open.SkillId));

            var trapdoor = Assert.Single(InteractiveRegistry.OnMap(Sky), i => i.Element.Id == Trapdoor);
            Assert.Contains(trapdoor.Actions, a => a.Kind == InteractiveActionKind.Teleport && a.SkillId == 184);
        }

        /// <summary>
        /// The whole way through the cage: a walk that ends on it is answered as every walk is,
        /// and then the character is on the island.
        /// </summary>
        [Fact]
        public async Task Stopping_in_the_cage_takes_you_to_the_island()
        {
            if (!WorldLoaded()) return;

            await using var gm = await ClientPipe.OpenAsync(990_000_801, 990_000_801, Dungeon, "Carcelero");
            gm.Session.State.CellId = Cage;
            gm.Session.State.PendingMovementMapId = Dungeon;
            gm.Session.State.PendingMovementCellId = Cage;

            using (SessionContext.Push(gm.Session))
                await WorldMoveHandler.AllowMapExitAsync(gm.ToClient, ConnectionProtocol.Push("jqi"));

            Assert.Equal((Island, 342), (gm.Session.State.MapId, gm.Session.State.CellId));
        }
    }
}
