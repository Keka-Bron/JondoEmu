using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Xunit;

namespace Jondo.Unity.Tests.Movement
{
    public sealed class CellTriggerMovementTests
    {
        [Fact]
        public void Completed_movement_is_consumed_once_on_the_exact_destination()
        {
            var state = new SessionState
            {
                MapId = 192937990,
                CellId = 414,
                PendingMovementMapId = 192937990,
                PendingMovementCellId = 414,
            };

            Assert.True(WorldMoveHandler.TryTakeCompletedMovement(
                state, out long mapId, out int cellId));
            Assert.Equal(192937990, mapId);
            Assert.Equal(414, cellId);

            Assert.False(WorldMoveHandler.TryTakeCompletedMovement(
                state, out _, out _));
        }

        [Theory]
        [InlineData(188746247, 414)]
        [InlineData(192937990, 413)]
        public void Stale_movement_cannot_trigger_after_position_changed(long mapId, int cellId)
        {
            var state = new SessionState
            {
                MapId = mapId,
                CellId = cellId,
                PendingMovementMapId = 192937990,
                PendingMovementCellId = 414,
            };

            Assert.False(WorldMoveHandler.TryTakeCompletedMovement(
                state, out _, out _));
            Assert.Equal(0, state.PendingMovementMapId);
            Assert.Equal(-1, state.PendingMovementCellId);
        }
    }
}
