using System;
using System.IO;
using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// What an administrator puts on a map and takes off it: an NPC on his cell, a group of
    /// monsters on his cell, and both taken away again.
    /// </summary>
    [Collection("MapManager")]
    public class MapAdminTests
    {
        /// <summary>A map no other test stands on.</summary>
        private const long Map = 990_000_501;

        private static int AnyNpc()
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT Id FROM NpcTemplates WHERE Look IS NOT NULL AND Look <> '' ORDER BY Id LIMIT 1;";
            return Convert.ToInt32(command.ExecuteScalar() ?? 0);
        }

        [Fact]
        public void An_npc_goes_on_the_admins_cell_once_and_comes_off_again()
        {
            if (!File.Exists(Paths.WorldDb)) return;
            int npc = AnyNpc();

            var placed = MapAdmin.SpawnNpc(Map, npc, 300, 3);
            Assert.NotNull(placed);
            try
            {
                Assert.Equal((npc, 300, 3), (placed!.NpcId, placed.Cell, placed.Orientation));
                Assert.True(placed.Bones > 0, "an NPC with no look is drawn as a question mark");
                Assert.Contains(MapAdmin.On(Map).Npcs, n => n.ContextualId == placed.ContextualId && n.Cell == 300);

                // Not the same one twice on one cell; on another cell, yes, under another id.
                Assert.Null(MapAdmin.SpawnNpc(Map, npc, 300, 3));
                var second = MapAdmin.SpawnNpc(Map, npc, 301, 3)!;
                Assert.NotEqual(placed.ContextualId, second.ContextualId);

                Assert.True(MapAdmin.RemoveNpc(Map, placed.ContextualId));
                Assert.False(MapAdmin.RemoveNpc(Map, placed.ContextualId));

                // A new one after a removal never takes the id of one still standing.
                var third = MapAdmin.SpawnNpc(Map, npc, 302, 3)!;
                Assert.NotEqual(second.ContextualId, third.ContextualId);
            }
            finally
            {
                foreach (var n in MapAdmin.On(Map).Npcs) MapAdmin.RemoveNpc(Map, n.ContextualId);
            }
        }

        [Fact]
        public void An_unknown_npc_is_put_nowhere()
            => Assert.Null(MapAdmin.SpawnNpc(Map, int.MaxValue, 300, 1));

        /// <summary>
        /// A group on the admin's cell, each monster at the grade asked; and once taken off, the
        /// map stays empty instead of filling itself with fresh groups as after a fight.
        /// </summary>
        [Fact]
        public void A_group_goes_on_the_admins_cell_and_an_emptied_map_stays_empty()
        {
            if (!File.Exists(Paths.WorldDb)) return;
            if (MobSpawnManager.GetMonsterData(31) == null) MobSpawnManager.InitializeAndSpawnAll();
            var monster = MobSpawnManager.GetMonsterData(31);
            if (monster == null || monster.Grades.Count < 2) return;

            const long map = Map + 1;
            var group = MapAdmin.SpawnMonsters(map, 250, new[] { (31, 2), (31, 1) });
            Assert.NotNull(group);
            Assert.Equal(250, group!.CellId);
            var here = Assert.Single(MapAdmin.On(map).Groups);
            Assert.Equal(new[] { 2, 1 }, here.Members.Select(m => m.Grade).ToArray());

            Assert.True(MapAdmin.RemoveGroup(map, group.MobId));
            Assert.Empty(MapAdmin.On(map).Groups);
            Assert.Empty(MobSpawnManager.GetMobsForMap(map));
        }

        [Fact]
        public void More_than_a_fight_holds_is_no_group()
            => Assert.Null(MapAdmin.SpawnMonsters(Map + 2, 250, Enumerable.Repeat((31, 1), MapAdmin.MaxMembers + 1).ToList()));
    }
}
