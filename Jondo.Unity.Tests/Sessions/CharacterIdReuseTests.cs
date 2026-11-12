using System;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.Sessions
{
    /// <summary>
    /// A new character never takes the id of one that was deleted, nor anything that one left.
    /// </summary>
    /// <remarks>
    /// The id used to be the largest in Characters plus one, so deleting the newest character and
    /// making another handed the same id out again. Whatever the deletion had not emptied -- the
    /// alignment among others -- then belonged to the newcomer: a brand new character came out of
    /// the creation sworn to Bonta, wings and all, and the client, which draws the wings in the
    /// ornament's place, no longer showed the ornament chosen for him.
    /// </remarks>
    public sealed class CharacterIdReuseTests
    {
        private const long Account = 8_900_000_093;

        private static long Create(string name)
        {
            long id = DatabaseManager.CreateCharacter(Account, 1, name, 11, 0, 1,
                new long[] { -1, -1, -1, -1, -1 }, 191105026, 1, 0, 0, Array.Empty<(int, int)>());
            Assert.True(id > 0);
            return id;
        }

        private static long Rows(string table, long characterId)
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE CharacterId = $id;";
            command.Parameters.AddWithValue("$id", characterId);
            return (long)command.ExecuteScalar()!;
        }

        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        [Fact]
        public void A_deleted_character_s_id_is_not_handed_out_again()
        {
            long first = Create("Relevo-Uno");
            Alignments.Set(first, Alignments.Bonta, Now);
            Alignments.SetPvp(first, true);
            Assert.NotEqual("", DatabaseManager.DeleteCharacter(first, Account));

            long second = Create("Relevo-Dos");
            try
            {
                Assert.True(second > first);
                Assert.False(Alignments.Of(second).Sworn);
                Assert.False(Alignments.Of(second).PvpOn);
            }
            finally { DatabaseManager.DeleteCharacter(second, Account); }
        }

        [Fact]
        public void Deleting_a_character_empties_his_alignment()
        {
            long id = Create("Relevo-Tres");
            Alignments.Set(id, Alignments.Brakmar, Now);
            Assert.Equal(1, Rows("CharacterAlignments", id));

            Assert.NotEqual("", DatabaseManager.DeleteCharacter(id, Account));
            Assert.Equal(0, Rows("CharacterAlignments", id));
        }

        /// <summary>
        /// What an older deletion left behind still counts: its rows keep the id taken, so the
        /// next character is made past it and starts unsworn.
        /// </summary>
        [Fact]
        public void An_id_whose_rows_an_older_deletion_left_is_not_handed_out()
        {
            long old = Create("Relevo-Cuatro");
            Alignments.Set(old, Alignments.Bonta, Now);
            using (var connection = new SqliteConnection(DatabaseManager.WorldConnectionString))
            {
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM Characters WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", old);
                command.ExecuteNonQuery();
            }

            long next = Create("Relevo-Cinco");
            try
            {
                Assert.True(next > old);
                Assert.False(Alignments.Of(next).Sworn);
            }
            finally
            {
                DatabaseManager.DeleteCharacter(next, Account);
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM CharacterAlignments WHERE CharacterId = $id;";
                command.Parameters.AddWithValue("$id", old);
                command.ExecuteNonQuery();
            }
        }
    }
}
