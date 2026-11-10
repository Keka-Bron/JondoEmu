using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Astrub bank: the vault door on the square leads into its room, the room's two exits lead
    /// back, and inside stand its own bankers, Orroha Neuw and Al Etsop, with Abel Hoz. See
    /// content/interactives/teleports.json and content/npcs/spawns.json.
    /// </summary>
    [Collection("MapManager")]
    public class AstrubBankTests
    {
        private const long Square = 191104002, Room = 192415750;
        private const int Door = 515308, DoorCell = 288, Landing = 410, ExitLanding = 302;

        private static bool WorldLoaded()
        {
            if (!MapManager.WalkableCells.ContainsKey(Square)) MapManager.Initialize();
            if (MapManager.GetMapInfo(Square) == null || MapManager.GetMapInfo(Room) == null) return false;
            Interactives.Initialize();
            TeleportManager.Initialize();
            return true;
        }

        [Fact]
        public void The_vault_door_leads_into_the_bank_and_its_exits_back()
        {
            if (!WorldLoaded()) return;   // no world data

            Assert.True(TeleportManager.TryGet(Square, Door, out var into));
            Assert.Equal((DoorCell, Room, Landing), (into.SourceCellId, into.DestinationMapId, into.DestinationCellId));
            Assert.Equal((-1, 184), (into.InteractiveType, into.SkillId));
            Assert.Contains(Landing, MapManager.WalkableCells[Room]);

            foreach (int exit in new[] { 515304, 515305 })
            {
                Assert.True(TeleportManager.TryGet(Room, exit, out var back));
                Assert.Equal((Square, ExitLanding), (back.DestinationMapId, back.DestinationCellId));
                Assert.Equal(339, back.SkillId);
                Assert.True(MapGeometry.Distance(back.SourceCellId, Landing) <= 2);
            }
            Assert.Contains(ExitLanding, MapManager.WalkableCells[Square]);
            Assert.Equal(1, MapGeometry.Distance(ExitLanding, DoorCell));
        }

        [Fact]
        public void The_door_is_declared_to_the_client()
        {
            if (!WorldLoaded()) return;
            InteractiveRegistry.Initialize();

            var door = Assert.Single(InteractiveRegistry.OnMap(Square), i => i.Element.Id == Door);
            var open = Assert.Single(door.Actions);
            Assert.Equal((InteractiveActionKind.Teleport, 184), (open.Kind, open.SkillId));
        }

        /// <summary>
        /// Inside, the bank's own two bankers behind their counter, facing it; not the generic owl.
        /// The counter is the unwalkable diagonal 258-272-287-301-316. Abel Hoz is placed in the
        /// room, but only comes in while «Ça tombe à l'eau» has him there: he is on the map, not
        /// shown to a character with no quest.
        /// </summary>
        [Fact]
        public void Its_own_bankers_stand_behind_the_counter()
        {
            Npcs.Initialize();
            Bankers.Initialize();
            var spawns = Npcs.OnMap(Room);
            var inside = spawns.Select(s => s.NpcId).ToList();
            if (inside.Count == 0) return;   // no world data

            var orroha = Assert.Single(spawns, s => s.NpcId == 520);
            var alEtsop = Assert.Single(spawns, s => s.NpcId == 522);
            Assert.Equal((273, 3), (orroha.Cell, orroha.Orientation));
            Assert.Equal((317, 3), (alEtsop.Cell, alEtsop.Orientation));
            // Each one next to the counter, which is not walkable.
            var floor = MapManager.WalkableCells.ContainsKey(Room) ? MapManager.WalkableCells[Room] : null;
            if (floor != null)
                foreach (int counter in new[] { 287, 330 }) Assert.DoesNotContain(counter, floor);
            Assert.Equal(1, MapGeometry.Distance(273, 287));
            Assert.Equal(1, MapGeometry.Distance(317, 330));
            Assert.DoesNotContain(4410, inside);
            Assert.Contains(Npcs.Of(Room), s => s.NpcId == 4410 && s.Criterion.Length > 0);
            Assert.DoesNotContain(100, inside);
            Assert.NotNull(Bankers.Of(520));
            Assert.NotNull(Bankers.Of(522));
        }
    }
}
