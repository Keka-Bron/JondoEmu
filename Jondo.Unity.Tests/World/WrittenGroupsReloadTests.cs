using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Content;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Alone: these read the written groups again from files of their own, which takes the world's
    /// written groups off their maps and puts them back while they run.
    /// </summary>
    [CollectionDefinition("WrittenGroupsReload", DisableParallelization = true)]
    public class WrittenGroupsReloadCollection { }

    /// <summary>
    /// content/monsters/groups.json read again with the world running (the content reload): a map
    /// ends as a restart would leave it.
    /// </summary>
    /// <remarks>
    /// They run against the real world database when it is on the machine and skip when it is not.
    /// Each file written here is the real one with something more or something less, and the real
    /// one is read again once each test is over.
    /// </remarks>
    [Collection("WrittenGroupsReload")]
    public class WrittenGroupsReloadTests : IDisposable
    {
        /// <summary>A monster every world has, for the groups written here.</summary>
        private const int Monster = 31;

        private readonly string _file = Path.Combine(Path.GetTempPath(),
                                                     "jondo-reload-groups-" + Guid.NewGuid().ToString("N") + ".json");

        public void Dispose()
        {
            if (MobSpawnManager.VetoedCount > 0) MobSpawnManager.ReloadWritten();
            try { File.Delete(_file); } catch (IOException) { }
        }

        private static bool World()
        {
            if (!File.Exists(Paths.WorldDb)) return false;
            if (MobSpawnManager.VetoedCount == 0) MobSpawnManager.InitializeAndSpawnAll();
            return MobSpawnManager.VetoedCount > 0 && MapManager.Maps.Count > 0
                   && MobSpawnManager.GetMonsterData(Monster) != null;
        }

        private static string RealFile => Paths.ContentFile(MobGroupContent.AuthoredFile);

        private static JsonObject Group(long map, long id, int cell)
            => new JsonObject
            {
                ["map"] = map,
                ["group"] = id,
                ["cell"] = cell,
                ["members"] = new JsonArray(new JsonObject { ["monster"] = Monster, ["grade"] = 0 }),
            };

        private static JsonObject Tombstone(long map, long id)
            => new JsonObject { ["map"] = map, ["group"] = id, ["remove"] = true };

        private static bool IsTombstone(JsonNode? entry)
            => entry?["remove"] is JsonValue gone && gone.TryGetValue(out bool yes) && yes;

        /// <summary>The real file, with these entries more and without the tombstones on <paramref name="unerased"/>.</summary>
        private void Write(IEnumerable<JsonObject> more, long unerased = 0)
        {
            var root = File.Exists(RealFile)
                ? JsonNode.Parse(File.ReadAllText(RealFile))!.AsObject()
                : new JsonObject();
            if (root["groups"] is not JsonArray groups) root["groups"] = groups = new JsonArray();
            foreach (var entry in groups.ToList())
                if (IsTombstone(entry) && entry!["map"]!.GetValue<long>() == unerased) groups.Remove(entry);
            foreach (var entry in more) groups.Add(entry);
            File.WriteAllText(_file, root.ToJsonString());
        }

        /// <summary>The real file's entries: its maps, and the base groups it erases.</summary>
        private static (HashSet<long> Maps, List<(long Map, long Group)> Erased) TheFile()
        {
            var maps = new HashSet<long>();
            var erased = new List<(long, long)>();
            if (!File.Exists(RealFile)) return (maps, erased);
            var groups = JsonNode.Parse(File.ReadAllText(RealFile))!["groups"]?.AsArray() ?? new JsonArray();
            foreach (var entry in groups)
            {
                long map = entry!["map"]!.GetValue<long>();
                maps.Add(map);
                if (IsTombstone(entry)) erased.Add((map, entry["group"]!.GetValue<long>()));
            }
            return (maps, erased);
        }

        private static bool Untouched(long map, HashSet<long> written)
            => !written.Contains(map) && !MobSpawnManager.IsVetoed(map) && !DungeonManager.IsRoom(map);

        /// <summary>A group of Ankama's on a map the file does not touch, the map as it was loaded.</summary>
        private static (long Map, MobSpawnManager.MobGroup Group)? AnkamasGroup()
        {
            var written = TheFile().Maps;
            foreach (long map in MapManager.Maps.Keys.OrderBy(m => m))
            {
                if (!Untouched(map, written)) continue;
                int loaded = MobSpawnManager.LoadedGroups(map);
                if (loaded == 0) continue;
                var here = MobSpawnManager.GetMobsForMap(map);
                if (here.Count != loaded) continue;
                var group = here.FirstOrDefault(g => g.MobId <= ActorIds.PrimerMonstruo
                                                     && g.MobId > MobGroupContent.FirstAuthoredId && !g.Modular);
                if (group != null) return (map, group);
            }
            return null;
        }

        /// <summary>
        /// A map of Ankama's whose groups the file all erases -- what an author does to a map he
        /// wants for a quest of his own --, filled on the fly once asked for: the map, the ids the
        /// file erases and the fill. A map with no group of its own mostly lies in a subarea with
        /// none, and gets no fill; this one has its subarea's monsters.
        /// </summary>
        private (long Map, long[] Erased, List<MobSpawnManager.MobGroup> Fill)? FilledOnTheFly()
        {
            var written = TheFile().Maps;
            int tries = 0;
            foreach (long map in MapManager.Maps.Keys.OrderBy(m => m))
            {
                if (!Untouched(map, written) || MobSpawnManager.LoadedGroups(map) == 0) continue;
                var ankamas = MobSpawnManager.GetMobsForMap(map);
                if (ankamas.Count != MobSpawnManager.LoadedGroups(map)) continue;
                if (++tries > 20) break;

                var erased = ankamas.Select(g => g.MobId).ToArray();
                Write(erased.Select(id => Tombstone(map, id)));
                MobSpawnManager.ReloadWritten(_file);
                var fill = MobSpawnManager.GetMobsForMap(map);
                if (fill.Count > 0) return (map, erased, fill);
            }
            return null;
        }

        private static bool InTheBase(long map, long group)
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM MapMobs WHERE MapId = $map AND MobId = $group;";
            command.Parameters.AddWithValue("$map", map);
            command.Parameters.AddWithValue("$group", group);
            return Convert.ToInt64(command.ExecuteScalar()) > 0;
        }

        /// <summary>
        /// The Studio's fork of one of Ankama's groups, then «put back»: the tombstone and the fork
        /// leave the file, and Ankama's group stands again on its map.
        /// </summary>
        [Fact]
        public void A_group_the_file_no_longer_erases_stands_again()
        {
            if (!World()) return;
            var found = AnkamasGroup();
            Assert.NotNull(found);
            var (map, group) = found!.Value;
            int loaded = MobSpawnManager.LoadedGroups(map);
            const long Fork = -2_990_001;

            Write(new[] { Tombstone(map, group.MobId), Group(map, Fork, group.CellId) });
            MobSpawnManager.ReloadWritten(_file);
            var forked = MobSpawnManager.GetMobsForMap(map);
            Assert.DoesNotContain(forked, g => g.MobId == group.MobId);
            Assert.Contains(forked, g => g.MobId == Fork);

            Write(Array.Empty<JsonObject>());
            MobSpawnManager.ReloadWritten(_file);
            var back = MobSpawnManager.GetMobsForMap(map);
            var again = Assert.Single(back, g => g.MobId == group.MobId);
            Assert.Equal(group.Members.Count, again.Members.Count);
            Assert.DoesNotContain(back, g => g.MobId == Fork);
            Assert.Equal(loaded, MobSpawnManager.LoadedGroups(map));

            // And erased once more when the file says so again.
            Write(new[] { Tombstone(map, group.MobId) });
            MobSpawnManager.ReloadWritten(_file);
            Assert.DoesNotContain(MobSpawnManager.GetMobsForMap(map), g => g.MobId == group.MobId);
            Assert.Equal(loaded - 1, MobSpawnManager.LoadedGroups(map));
        }

        /// <summary>
        /// The groups the file erased when the world started stand again once it no longer erases
        /// them, and only they: a map that was filled on the fly for having nothing loses its fill.
        /// </summary>
        [Fact]
        public void The_groups_erased_at_the_start_stand_again_without_the_fill()
        {
            if (!World()) return;
            var erased = TheFile().Erased
                .Where(k => !MobSpawnManager.IsVetoed(k.Map) && !DungeonManager.IsRoom(k.Map) && InTheBase(k.Map, k.Group))
                .ToList();
            if (erased.Count == 0) return;
            // A map all of whose groups the file erases, if there is one: it is filled on the fly.
            long map = erased.Select(k => k.Map).FirstOrDefault(m => MobSpawnManager.LoadedGroups(m) == 0, erased[0].Map);
            var ids = erased.Where(k => k.Map == map).Select(k => k.Group).ToHashSet();
            int loaded = MobSpawnManager.LoadedGroups(map);
            Assert.DoesNotContain(MobSpawnManager.GetMobsForMap(map), g => ids.Contains(g.MobId));

            Write(Array.Empty<JsonObject>(), unerased: map);
            MobSpawnManager.ReloadWritten(_file);
            var here = MobSpawnManager.GetMobsForMap(map);
            Assert.All(ids, id => Assert.Contains(here, g => g.MobId == id));
            Assert.Equal(loaded + ids.Count, MobSpawnManager.LoadedGroups(map));
            if (loaded == 0) Assert.Equal(ids.OrderBy(i => i), here.Select(g => g.MobId).OrderBy(i => i));

            MobSpawnManager.ReloadWritten();
            Assert.DoesNotContain(MobSpawnManager.GetMobsForMap(map), g => ids.Contains(g.MobId));
            Assert.Equal(loaded, MobSpawnManager.LoadedGroups(map));
        }

        /// <summary>
        /// A map filled on the fly that the file now writes a group for keeps that group alone, as a
        /// restart would leave it -- not the fill, whose beaten groups would be drawn again and
        /// again --; a criminal standing in the fill stands nowhere.
        /// </summary>
        [Fact]
        public void A_map_filled_on_the_fly_keeps_only_its_written_group()
        {
            if (!World()) return;
            var found = FilledOnTheFly();
            Assert.NotNull(found);
            var (map, erased, fill) = found!.Value;
            Assert.All(fill, g => Assert.DoesNotContain(g.MobId, erased));
            const long Written = -2_990_002;
            const int Criminal = 999_990_002;
            WantedPosters.Placed(Criminal, map, fill[0].MobId);

            Write(erased.Select(id => Tombstone(map, id)).Append(Group(map, Written, fill[0].CellId)));
            MobSpawnManager.ReloadWritten(_file);

            Assert.Equal(new[] { Written }, MobSpawnManager.GetMobsForMap(map).Select(g => g.MobId));
            Assert.False(WantedPosters.IsAbout(Criminal));
        }

        /// <summary>A criminal standing in a written group read again stands nowhere, and may come back elsewhere.</summary>
        [Fact]
        public void A_criminal_in_a_written_group_read_again_is_let_go()
        {
            if (!World()) return;
            const long Map = 990_000_701;
            const long Written = -2_990_003;
            const int Criminal = 999_990_003;

            Write(new[] { Group(Map, Written, 300) });
            MobSpawnManager.ReloadWritten(_file);
            Assert.Contains(MobSpawnManager.GetMobsForMap(Map), g => g.MobId == Written);
            WantedPosters.Placed(Criminal, Map, Written);

            // The same file again: a fresh copy of the group, without him.
            MobSpawnManager.ReloadWritten(_file);
            Assert.False(WantedPosters.IsAbout(Criminal));
        }

        /// <summary>A criminal standing in one of Ankama's groups the file now erases stands nowhere.</summary>
        [Fact]
        public void A_criminal_in_a_group_the_file_erases_is_let_go()
        {
            if (!World()) return;
            var found = AnkamasGroup();
            Assert.NotNull(found);
            var (map, group) = found!.Value;
            const int Criminal = 999_990_004;
            WantedPosters.Placed(Criminal, map, group.MobId);

            Write(new[] { Tombstone(map, group.MobId) });
            MobSpawnManager.ReloadWritten(_file);

            Assert.DoesNotContain(MobSpawnManager.GetMobsForMap(map), g => g.MobId == group.MobId);
            Assert.False(WantedPosters.IsAbout(Criminal));
        }
    }
}
