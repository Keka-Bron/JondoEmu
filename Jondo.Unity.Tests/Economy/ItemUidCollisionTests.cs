using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.Economy
{
    /// <summary>
    /// Another writer took the uid the counter hands out next -- a second process on the same
    /// base, a test run pointed at it. A dropped hat got that uid, its insert was refused and it
    /// was lost without a word. Now the counter is set above the base's highest uid again and the
    /// item written once more, whether a piece of its own or a new stack.
    /// </summary>
    [Collection("conquests")]
    public class ItemUidCollisionTests : IDisposable
    {
        private const long Owner = 8_940_200_001, Other = 8_940_200_002;
        private const int Hat = 8247, Feather = 6899;

        [Fact]
        public void A_piece_whose_uid_another_took_is_written_under_a_new_one()
        {
            long taken = TakeTheNextUid();
            var piece = DatabaseManager.AddPieceToInventory(Owner, Hat, "[]");

            Assert.NotNull(piece);
            Assert.NotEqual(taken, piece!.Uid);
            Assert.Equal(Hat, GidOf(piece.Uid, Owner));
        }

        [Fact]
        public void A_new_stack_whose_uid_another_took_is_written_under_a_new_one()
        {
            long taken = TakeTheNextUid();
            var added = DatabaseManager.AddItemsToInventory(Owner, new Dictionary<int, int> { [Feather] = 2 });

            var stack = Assert.Single(added);
            Assert.NotEqual(taken, stack.Uid);
            Assert.Equal(Feather, GidOf(stack.Uid, Owner));
            Assert.Equal(Hat, GidOf(taken, Other));   // the other writer's row untouched
        }

        /// <summary>The uid the counter hands out next, written first by somebody else.</summary>
        private static long TakeTheNextUid()
        {
            long taken = DatabaseManager.NextItemUid() + 1;
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO CharacterItems (CharacterId, Uid, Gid, Quantity, Position, Effects) " +
                                  "VALUES ($id, $uid, $gid, 1, 63, '[]');";
            command.Parameters.AddWithValue("$id", Other);
            command.Parameters.AddWithValue("$uid", taken);
            command.Parameters.AddWithValue("$gid", Hat);
            command.ExecuteNonQuery();
            return taken;
        }

        private static int? GidOf(long uid, long owner)
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT Gid FROM CharacterItems WHERE Uid = $uid AND CharacterId = $id;";
            command.Parameters.AddWithValue("$uid", uid);
            command.Parameters.AddWithValue("$id", owner);
            return command.ExecuteScalar() is long gid ? (int)gid : null;
        }

        public void Dispose()
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM CharacterItems WHERE CharacterId IN ($a, $b);";
            command.Parameters.AddWithValue("$a", Owner);
            command.Parameters.AddWithValue("$b", Other);
            command.ExecuteNonQuery();
        }
    }
}
