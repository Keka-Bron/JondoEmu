using System.Linq;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Movement
{
    /// <summary>
    /// jqe: the client cut its walk short on a cell. The character stands there, and the cut walk's
    /// end no longer fires a passage. In the MapManager collection, as every test that loads the maps.
    /// </summary>
    [Collection("MapManager")]
    public sealed class StopMovementTests
    {
        private const long Map = 192937990;

        [Fact]
        public void A_walk_cut_short_leaves_the_character_where_it_stopped()
        {
            if (!MapManager.WalkableCells.ContainsKey(Map)) MapManager.Initialize();
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(DatabaseManager.WorldConnectionString))
            {
                connection.Open();
                DatabaseManager.MoveScrollsOutOfTheBase(connection);
            }
            int stop = MapManager.WalkableCells[Map].First(c => c != 414);
            var session = GameSession.SinSocket();
            session.State.MapId = Map;
            session.State.CellId = 414;
            session.State.PendingMovementMapId = Map;
            session.State.PendingMovementCellId = 414;
            using (SessionContext.Push(session))
            {
                WorldMoveHandler.StopMovement(session, new MapMovementCancelRequest(stop));
                Assert.Equal(stop, session.State.CellId);
                Assert.False(WorldMoveHandler.TryTakeCompletedMovement(session.State, out _, out _));

                // A cell the map does not walk on changes nothing.
                WorldMoveHandler.StopMovement(session, new MapMovementCancelRequest(9999));
                Assert.Equal(stop, session.State.CellId);
            }
        }
    }
}
