using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.Economy
{
    /// <summary>
    /// Something given twice, the way a quest's reply hands out one larva at a time: the second
    /// joined no stack and the bag showed six rows of one larva each.
    /// </summary>
    [Collection("forgemagic")]
    public class GrantOntoAStackTests
    {
        private const int Larva = 15128;
        private const long Character = 8_940_100_021, Account = 8_940_000_021;

        [Fact]
        public async Task The_second_one_joins_the_first()
        {
            RecyclingTests.ClearBag(Character);
            try
            {
                await using var pipe = await ClientPipe.OpenAsync(Account, Character, 191105026, "Larva");
                using (SessionContext.Push(pipe.Session))
                {
                    Equipment.LoadFrom(Character);
                    var first = await Equipment.GrantAsync(pipe.ToClient, Larva, 1);
                    if (first == null) return;   // no item data
                    Assert.Equal(Op.Iua, (await pipe.NextAny()).Opcode);

                    var second = await Equipment.GrantAsync(pipe.ToClient, Larva, 1);

                    // The same item, told to the client as its new count: an iua would be added on
                    // top of the stack the client already shows.
                    Assert.Equal(first.Uid, second!.Uid);
                    Assert.Equal(Op.Ivj, (await pipe.NextAny()).Opcode);
                    Assert.Equal(2, Equipment.HowMany(Larva));
                    Assert.Equal(1, RowsOf(Larva));
                }
            }
            finally
            {
                RecyclingTests.ClearBag(Character);
            }
        }

        private static long RowsOf(int gid)
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM CharacterItems WHERE CharacterId = $c AND Gid = $g;";
            command.Parameters.AddWithValue("$c", Character);
            command.Parameters.AddWithValue("$g", gid);
            return (long)command.ExecuteScalar()!;
        }
    }
}
