using System.IO;
using System.Linq;
using Jondo.Unity.Server.Handlers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// The iron scissors of «Los mechones de la gloria»: one lock of hair for each of the nineteen
    /// classes, each a quest item of the client's catalogue.
    /// </summary>
    public class LockOfHairTests
    {
        [Fact]
        public void Every_class_has_its_own_lock_of_hair()
        {
            var locks = ItemOnCharacterHandler.LockOfHair;
            Assert.Equal(19, locks.Count);
            Assert.Equal(19, locks.Values.Distinct().Count());
            Assert.DoesNotContain(19, locks.Keys);

            if (!File.Exists(Jondo.Unity.Launcher.Paths.WorldDb)) return;
            using var db = new SqliteConnection(Jondo.Unity.Launcher.Paths.WorldConnectionString);
            db.Open();
            foreach (int item in locks.Values)
            {
                using var command = db.CreateCommand();
                command.CommandText = "SELECT Type FROM ItemTemplates WHERE Id = $id";
                command.Parameters.AddWithValue("$id", item);
                var type = command.ExecuteScalar();
                Assert.NotNull(type);
                Assert.Equal(133L, (long)type!);
            }
        }
    }
}
