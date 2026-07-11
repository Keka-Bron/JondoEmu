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
        private static Dictionary<int, List<int>> _subareas = new Dictionary<int, List<int>>();
        private static Dictionary<long, int> _mapSubareas = new Dictionary<long, int>();
        private static Dictionary<long, List<MobGroup>> _mapMobs = new Dictionary<long, List<MobGroup>>();

        public static void InitializeAndSpawnAll()
        {
            Console.WriteLine("[MobSpawnManager] Loading data from SQLite and spawning mobs...");
            
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

            // Load Subareas
            var cmdSubareas = connection.CreateCommand();
            cmdSubareas.CommandText = "SELECT Id, Monsters FROM Subareas;";
            using (var reader = cmdSubareas.ExecuteReader())
            {
                while (reader.Read())
                {
                    var id = reader.GetInt32(0);
                    var list = new List<int>();
                    string monstersJson = reader.GetString(1);
                    try {
                        using var doc = System.Text.Json.JsonDocument.Parse(monstersJson);
                        if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach(var m in doc.RootElement.EnumerateArray()) {
                                list.Add(m.GetInt32());
                            }
                        }
                    } catch {}
                    _subareas[id] = list;
                }
            }

            // Load MapSubareas
            var cmdMapSubareas = connection.CreateCommand();
            cmdMapSubareas.CommandText = "SELECT MapId, SubAreaId FROM MapSubareas;";
            using (var reader = cmdMapSubareas.ExecuteReader())
            {
                while (reader.Read())
                {
                    _mapSubareas[reader.GetInt64(0)] = reader.GetInt32(1);
                }
            }

            // Spawn mobs
            long currentMobId = -1000000;
            Random rand = new Random();

            foreach(var kvp in _mapSubareas)
            {
                long mapId = kvp.Key;
                int subAreaId = kvp.Value;

                if (!_subareas.TryGetValue(subAreaId, out var allowedMonsters) || allowedMonsters.Count == 0)
                    continue;

                var validMonsters = allowedMonsters.Where(id => _monsters.ContainsKey(id)).ToList();
                if (validMonsters.Count == 0) continue;

                int numMobs = rand.Next(1, 5); // 1 to 4 mobs
                var mobs = new List<MobGroup>();

                for(int i = 0; i < numMobs; i++)
                {
                    int groupSize = rand.Next(1, 9); // 1 to 8 monsters
                    var mob = new MobGroup {
                        MobId = currentMobId--,
                        CellId = rand.Next(50, 450)
                    };

                    for(int m = 0; m < groupSize; m++)
                    {
                        int monsterId = validMonsters[rand.Next(validMonsters.Count)];
                        var mData = _monsters[monsterId];
                        int gradeIdx = 0;
                        int lvl = 1;
                        if (mData.Grades.Count > 0) {
                            gradeIdx = rand.Next(mData.Grades.Count);
                            lvl = mData.Grades[gradeIdx].Level;
                        }
                        mob.Members.Add(new MobMember {
                            Monster = mData,
                            GradeIndex = gradeIdx,
                            Level = lvl
                        });
                    }
                    mobs.Add(mob);
                }
                _mapMobs[mapId] = mobs;
            }

            Console.WriteLine($"[MobSpawnManager] Spawned {Math.Abs(currentMobId + 1000000)} mobs across {_mapMobs.Count} maps.");
        }

        public static List<MobGroup> GetMobsForMap(long mapId)
        {
            if (_mapMobs.TryGetValue(mapId, out var mobs))
                return mobs;
            return new List<MobGroup>();
        }
    }
}
