using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Incarnam's monster groups are as big as on the official server. Packet captures of 20
    /// Incarnam maps (2026-11-17 to 11-20) listed 76 groups: 43 of one monster, 23 of two, 9 of three
    /// and 1 of four. The seeding drew 1 to 8 evenly.
    /// </summary>
    public sealed class IncarnamGroupSizeTests
    {
        [Fact]
        public void The_rolls_give_the_measured_spread()
        {
            var sizes = Enumerable.Range(0, MobSpawnManager.IncarnamGroupRolls)
                .Select(MobSpawnManager.IncarnamGroupSize)
                .GroupBy(size => size)
                .ToDictionary(g => g.Key, g => g.Count());

            Assert.Equal(43, sizes[1]);
            Assert.Equal(23, sizes[2]);
            Assert.Equal(9, sizes[3]);
            Assert.Equal(1, sizes[4]);
            Assert.Equal(4, sizes.Count);
        }

        [Fact]
        public void Incarnam_is_area_45_and_only_it()
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString + ";Mode=ReadOnly");
            try { connection.Open(); } catch (SqliteException) { return; }   // no data
            var incarnam = MobSpawnManager.LoadIncarnam(connection);
            if (incarnam.Count == 0) return;   // no data

            Assert.Contains(154010371L, incarnam);       // Incarnam's zaap, Caminho das almas [-1,-3]
            Assert.Contains(153879813L, incarnam);       // the Pastures' zaap [2,-5]
            Assert.DoesNotContain(191105026L, incarnam); // Astrub's zaap [5,-18]
        }
    }
}
