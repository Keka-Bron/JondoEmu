using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.Linq;

namespace Jondo.Unity.Launcher.Managers
{
    public static class MobSpawnManager
    {
        public class MonsterGrade
        {
            public int Level { get; set; }
        }

        public class MonsterData
        {
            public int Id { get; set; }
            public int NameId { get; set; }
            public string Look { get; set; }
            public List<MonsterGrade> Grades { get; set; } = new List<MonsterGrade>();
        }

        public class MobMember
        {
            public MonsterData Monster { get; set; }
            public int GradeIndex { get; set; }
            public int Level { get; set; }
        }

        public class MobGroup
        {
            public long MobId { get; set; }
            public int CellId { get; set; }
            public List<MobMember> Members { get; set; } = new List<MobMember>();
        }

        private static Dictionary<int, MonsterData> _monsters = new Dictionary<int, MonsterData>();
        private static Dictionary<long, List<MobGroup>> _mapMobs = new Dictionary<long, List<MobGroup>>();

        public static void InitializeAndSpawnAll()
        {
            Console.WriteLine("[MobSpawnManager] Loading data from SQLite...");
            
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();

            // Load Monsters
            var cmdMonsters = connection.CreateCommand();
            cmdMonsters.CommandText = "SELECT Id, NameId, Look, Grades FROM Monsters;";
            using (var reader = cmdMonsters.ExecuteReader())
            {
                while (reader.Read())
                {
                    var id = reader.GetInt32(0);
                    var data = new MonsterData {
                        Id = id,
                        NameId = reader.GetInt32(1),
                        Look = reader.GetString(2)
                    };
                    string gradesJson = reader.GetString(3);
                    try {
                        using var doc = System.Text.Json.JsonDocument.Parse(gradesJson);
                        if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach(var g in doc.RootElement.EnumerateArray()) {
                                int lvl = g.TryGetProperty("level", out var l) ? l.GetInt32() : 1;
                                data.Grades.Add(new MonsterGrade { Level = lvl });
                            }
                        }
                    } catch {}
                    _monsters[id] = data;
                }
            }

            // Load MapMobs
            var cmdMapMobs = connection.CreateCommand();
            cmdMapMobs.CommandText = "SELECT MapId, MobId, CellId, MembersJson FROM MapMobs;";
            int count = 0;
            using (var reader = cmdMapMobs.ExecuteReader())
            {
                while (reader.Read())
                {
                    long mapId = reader.GetInt64(0);
                    long mobId = reader.GetInt64(1);
                    int cellId = reader.GetInt32(2);
                    string membersJson = reader.GetString(3);

                    var group = new MobGroup {
                        MobId = mobId,
                        CellId = cellId
                    };

                    try {
                        using var doc = System.Text.Json.JsonDocument.Parse(membersJson);
                        if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach(var m in doc.RootElement.EnumerateArray()) {
                                int mId = m.GetProperty("id").GetInt32();
                                int grade = m.GetProperty("grade").GetInt32();
                                int level = m.GetProperty("level").GetInt32();

                                if (_monsters.TryGetValue(mId, out var mData)) {
                                    group.Members.Add(new MobMember {
                                        Monster = mData,
                                        GradeIndex = grade,
                                        Level = level
                                    });
                                }
                            }
                        }
                    } catch {}

                    if (!_mapMobs.ContainsKey(mapId))
                        _mapMobs[mapId] = new List<MobGroup>();
                    _mapMobs[mapId].Add(group);
                    count++;
                }
            }

            Console.WriteLine($"[MobSpawnManager] Loaded {count} mobs across {_mapMobs.Count} maps.");
        }

        public static List<MobGroup> GetMobsForMap(long mapId)
        {
            if (_mapMobs.TryGetValue(mapId, out var mobs))
                return mobs;
            return new List<MobGroup>();
        }
    }
}
