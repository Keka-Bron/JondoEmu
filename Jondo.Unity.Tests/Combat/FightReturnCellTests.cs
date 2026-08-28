using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    public class FightReturnCellTests
    {
        [Fact]
        public void Entering_an_arena_keeps_the_surface_cell_instead_of_the_placement_cell()
        {
            const long mapId = 991_000_001;
            WithWalkableCells(mapId, new[] { 176, 190 }, () =>
            {
                var state = new SessionState { MapId = mapId, CellId = 176 };

                FightHandler.RememberRoleplayReturn(state, state.MapId, state.CellId);
                state.MapId = 992_000_001;
                state.CellId = 177;

                Assert.Equal(mapId, state.RoleplayMapId);
                Assert.Equal(176, state.RoleplayCellId);
            });
        }

        [Fact]
        public void An_invalid_surface_cell_is_replaced_by_the_nearest_walkable_one()
        {
            const long mapId = 991_000_002;
            WithWalkableCells(mapId, new[] { 176, 190 }, () =>
            {
                Assert.Equal(176, FightHandler.SafeRoleplayReturnCell(mapId, 177));
            });
        }

        private static void WithWalkableCells(long mapId, int[] cells, Action assertion)
        {
            bool existed = MapManager.WalkableCells.TryGetValue(mapId, out var previous);
            MapManager.WalkableCells[mapId] = cells.ToList();
            try
            {
                assertion();
            }
            finally
            {
                if (existed)
                {
                    MapManager.WalkableCells[mapId] = previous!;
                }
                else
                {
                    MapManager.WalkableCells.Remove(mapId);
                }
            }
        }
    }
}
