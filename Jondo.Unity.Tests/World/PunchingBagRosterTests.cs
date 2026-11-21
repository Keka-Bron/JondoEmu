using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Kanojedo's punching bag is placed by hand, in the Kanojedo and the dojo, and nowhere
    /// else. The tables list it among Amakna's subareas, and a group of the level-13 monsters of an
    /// ordinary map came out with it, level 100 and ten thousand life points.
    /// </summary>
    public sealed class PunchingBagRosterTests
    {
        private const int AmaknaSubarea = 10;

        [Fact]
        public void No_subarea_roster_puts_the_punching_bag_in_its_groups()
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString + ";Mode=ReadOnly");
            try { connection.Open(); } catch (SqliteException) { return; }   // no data
            var rosters = MobSpawnManager.LoadSubareaRosters(connection);
            if (!rosters.TryGetValue(AmaknaSubarea, out var amakna) || amakna.Count == 0) return;   // no data

            Assert.DoesNotContain(MobSpawnManager.PunchingBag, amakna);
            Assert.All(rosters.Values, roster => Assert.DoesNotContain(MobSpawnManager.PunchingBag, roster));
            Assert.False(MobSpawnManager.Roams(MobSpawnManager.PunchingBag));
        }
    }
}
