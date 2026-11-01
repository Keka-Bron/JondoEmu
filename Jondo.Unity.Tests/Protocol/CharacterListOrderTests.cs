using System;
using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.Protocol
{
    /// <summary>
    /// The character list (kvi) goes the one played last first, as the real server sends it; the
    /// client shows it as it comes, and after a creation plays its first.
    /// </summary>
    public class CharacterListOrderTests
    {
        private const long Account = 8_970_000_001;

        private static DatabaseManager.DbCharacter Character(long id, string lastConnection)
            => new() { Id = id, AccountId = Account, Name = "Orden" + id, Level = 1, Breed = 1, LastConnection = lastConnection };

        private static long[] IdsOf(byte[] kvi)
            => ProtoMessage.Parse(kvi).Fields.Where(f => f.FieldNumber == 1)
                .Select(e => ProtoMessage.Parse(e.BytesValue).Fields.Single(f => f.FieldNumber == 2).VarIntValue)
                .ToArray();

        [Fact]
        public void The_character_played_last_comes_first()
        {
            var characters = new[]
            {
                Character(1, "2026-03-02T10:00:00.000+01:00"),
                Character(2, ""),
                Character(3, "2026-03-04T18:45:00.000+02:00"),
                // Earlier on the clock than the one above, later in time: another offset.
                Character(4, "2026-03-04T18:15:00.000+01:00"),
                Character(5, "2026-02-20T09:30:00.000+01:00"),
            };

            Assert.Equal(new long[] { 4, 3, 1, 5, 2 }, IdsOf(ConnectionProtocol.BuildCharactersList(characters)));
        }

        [Fact]
        public void Equal_or_missing_dates_keep_the_order_they_came_in()
        {
            var characters = new[]
            {
                Character(7, ""), Character(8, "2026-08-09T10:00:00.000+02:00"),
                Character(9, ""), Character(10, "2026-08-09T10:00:00.000+02:00"),
            };

            Assert.Equal(new long[] { 8, 10, 7, 9 }, ConnectionProtocol.MostRecentFirst(characters).Select(c => c.Id));
        }

        [Fact]
        public void A_character_just_created_heads_the_list()
        {
            long old = 0, fresh = 0;
            try
            {
                old = DatabaseManager.CreateCharacter(Account, 1, "Orden-Viejo", 8, 0, 1, new long[] { -1, -1, -1, -1, -1 },
                                                      191105026, 1, 0, 0, Array.Empty<(int, int)>());
                Assert.True(old > 0);
                using (var connection = new SqliteConnection(DatabaseManager.WorldConnectionString))
                {
                    connection.Open();
                    using var played = connection.CreateCommand();
                    played.CommandText = "UPDATE Characters SET LastConnection = '2026-01-01T00:00:00.000+01:00' WHERE Id = $id;";
                    played.Parameters.AddWithValue("$id", old);
                    played.ExecuteNonQuery();
                }

                fresh = DatabaseManager.CreateCharacter(Account, 1, "Orden-Nuevo", 8, 0, 1, new long[] { -1, -1, -1, -1, -1 },
                                                        191105026, 1, 0, 0, Array.Empty<(int, int)>());
                Assert.True(fresh > 0);

                var list = DatabaseManager.GetCharactersByAccountId(Account, 1);
                Assert.NotEmpty(list.Single(c => c.Id == fresh).LastConnection);
                Assert.Equal(new[] { fresh, old }, IdsOf(ConnectionProtocol.BuildCharactersList(list)));
            }
            finally
            {
                if (old > 0) DatabaseManager.DeleteCharacter(old, Account);
                if (fresh > 0) DatabaseManager.DeleteCharacter(fresh, Account);
            }
        }
    }
}
