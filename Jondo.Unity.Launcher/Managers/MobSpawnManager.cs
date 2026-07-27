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

            DatabaseManager.EnsureMobsSeeded(connection);

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
                        var root = doc.RootElement;
                        if (root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("Array", out var arrProp))
                        {
                            root = arrProp;
                        }
                        if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach(var g in root.EnumerateArray()) {
                                int lvl = g.TryGetProperty("level", out var l) ? l.GetInt32() : 1;
                                data.Grades.Add(new MonsterGrade { Level = lvl });
                            }
                        }
                    } catch {}
                    _monsters[id] = data;
                }
            }

            _mapMobs.Clear();

            // Load MapMobs from SQLite database
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

            Console.WriteLine($"[MobSpawnManager] Loaded {count} persistent mobs across {_mapMobs.Count} maps from database.");
        }

        private static long _nextDynamicMobId = -2000000;
        private static Random _rand = new Random();

        public static List<MobGroup> GetMobsForMap(long mapId)
        {
            if (_mapMobs.TryGetValue(mapId, out var mobs) && mobs.Count > 0)
                return mobs;

            mobs = GenerateDynamicMobsForMap(mapId);
            _mapMobs[mapId] = mobs;
            return mobs;
        }

        private static List<MobGroup> GenerateDynamicMobsForMap(long mapId)
        {
            var result = new List<MobGroup>();
            if (_monsters.Count == 0) return result;

            var pioIds = new List<int> { 491, 492, 493, 463, 2341, 2342, 2343, 2344, 2345, 2347 };
            var availableMonsters = pioIds.Where(id => _monsters.ContainsKey(id)).ToList();
            if (availableMonsters.Count == 0)
            {
                availableMonsters = _monsters.Keys.Take(20).ToList();
            }

            var validCells = GetInnerWalkableCells(mapId);

            int numMobs = _rand.Next(2, 5); // 2 to 4 groups per map
            var usedCells = new HashSet<int>();

            for (int i = 0; i < numMobs; i++)
            {
                int groupSize = _rand.Next(1, 9); // 1 to 8 monsters per group (Dofus standard!)
                int cellId = validCells[_rand.Next(validCells.Count)];
                while (usedCells.Contains(cellId) && usedCells.Count < validCells.Count)
                {
                    cellId = validCells[_rand.Next(validCells.Count)];
                }
                usedCells.Add(cellId);

                var group = new MobGroup
                {
                    MobId = _nextDynamicMobId--,
                    CellId = cellId
                };

                for (int m = 0; m < groupSize; m++)
                {
                    int monsterId = availableMonsters[_rand.Next(availableMonsters.Count)];
                    var mData = _monsters[monsterId];
                    int gradeIdx = 0;
                    int lvl = 1;
                    if (mData.Grades.Count > 0)
                    {
                        gradeIdx = _rand.Next(mData.Grades.Count);
                        lvl = mData.Grades[gradeIdx].Level;
                    }

                    group.Members.Add(new MobMember
                    {
                        Monster = mData,
                        GradeIndex = gradeIdx,
                        Level = lvl
                    });
                }
                result.Add(group);
            }

            return result;
        }

        public static List<int> GetInnerWalkableCells(long mapId)
        {
            if (!MapManager.WalkableCells.TryGetValue(mapId, out var cells) || cells.Count == 0)
            {
                return new List<int> { 288, 303, 312, 327, 344, 350 };
            }

            var cellSet = new HashSet<int>(cells);
            var innerCells = new List<int>();

            // Offsets for radius 1 and radius 2 surrounding cells (12 neighbor cells total)
            int[] radiusOffsets = new int[]
            {
                -14, 14, -1, 1,          // Radius 1
                -28, 28, -2, 2,          // Radius 2 orthogonal
                -15, -13, 13, 15         // Radius 2 diagonal
            };

            foreach (var cell in cells)
            {
                int row = cell / 14;
                int col = cell % 14;

                // Exclude map borders
                if (row < 8 || row > 28 || col < 2 || col > 11) continue;

                // Verify all 12 cells in a radius of 2 steps are 100% walkable
                bool allWalkable = true;
                foreach (int offset in radiusOffsets)
                {
                    if (!cellSet.Contains(cell + offset))
                    {
                        allWalkable = false;
                        break;
                    }
                }

                if (allWalkable)
                {
                    innerCells.Add(cell);
                }
            }

            return innerCells.Count > 0 ? innerCells : cells;
        }
    }
}
