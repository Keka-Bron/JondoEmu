using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// La Bête is the vaccine quest's boss, fought in Albert Pess's cellar. Frigost Village's
    /// subarea roster and hundreds of MapMobs groups carried it into ordinary walking groups.
    /// </summary>
    public sealed class LaBeteSpawnTests
    {
        private const long CarversWorkshop = 54169896;
        private const long Cellar = MobSpawnManager.LaBeteBasement;
        private const int Beast = MobSpawnManager.LaBete;

        [Fact]
        public void La_Bete_only_roams_in_the_quests_cellar()
        {
            Assert.False(MobSpawnManager.Roams(Beast));
            Assert.False(MobSpawnManager.Roams(CarversWorkshop, Beast));
            Assert.True(MobSpawnManager.Roams(Cellar, Beast));
            Assert.True(MobSpawnManager.Roams(CarversWorkshop, 2913));
        }

        [Fact]
        public void The_subarea_rosters_keep_it_out_of_the_village()
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString + ";Mode=ReadOnly");
            try { connection.Open(); } catch (SqliteException) { return; }   // no data

            var rosters = MobSpawnManager.LoadSubareaRosters(connection);
            Assert.All(rosters.Values, roster => Assert.DoesNotContain(Beast, roster));
        }
    }
}
