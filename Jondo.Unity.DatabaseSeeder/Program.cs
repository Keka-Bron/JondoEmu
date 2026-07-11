using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.Linq;
using System.Text.Json;

namespace Jondo.Unity.DatabaseSeeder
{
    class Program
    {
        public class MonsterGrade { public int Level { get; set; } }
        public class MonsterData { public int Id { get; set; } public int NameId { get; set; } public string Look { get; set; } public List<MonsterGrade> Grades { get; set; } = new List<MonsterGrade>(); }
        public class MobMember { public int id { get; set; } public int grade { get; set; } public int level { get; set; } }
        
        static void Main(string[] args)
        {
            string dbPath = "C:/Jondo/world.db";
            string connStr = $"Data Source={dbPath}";
            using var connection = new SqliteConnection(connStr);
            connection.Open();

            Console.WriteLine("[Seeder] Creating tables if not exist...");
            var createCmd = connection.CreateCommand();
            createCmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS MapMobs (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    MapId INTEGER NOT NULL,
                    MobId INTEGER NOT NULL,
                    CellId INTEGER NOT NULL,
                    MembersJson TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS CharacterItems (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    CharacterId INTEGER NOT NULL,
                    Uid INTEGER NOT NULL,
                    Gid INTEGER NOT NULL,
                    Quantity INTEGER NOT NULL DEFAULT 1,
                    Position INTEGER NOT NULL DEFAULT 63,
                    Effects TEXT
                );
                CREATE TABLE IF NOT EXISTS Monsters (
                    Id INTEGER PRIMARY KEY,
                    NameId INTEGER NOT NULL,
                    Look TEXT NOT NULL,
                    Grades TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS Subareas (
                    Id INTEGER PRIMARY KEY,
                    Monsters TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS MapSubareas (
                    MapId INTEGER PRIMARY KEY,
                    SubAreaId INTEGER NOT NULL
                );
            ";
            createCmd.ExecuteNonQuery();

            Console.WriteLine("[Seeder] Populating JSON...");
            PopulateMonstersFromJSON(connection);

            Console.WriteLine("[Seeder] Populating Mobs...");
            PopulateMobs(connection);

            Console.WriteLine("[Seeder] Giving Level 200 items to Character 1...");
            GiveLevel200Items(connection, 1);

            Console.WriteLine("[Seeder] Done.");
        }

        static void PopulateMonstersFromJSON(SqliteConnection connection)
        {
            var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM Monsters;";
            long count = (long)checkCmd.ExecuteScalar();
            if (count > 0) return;

            Console.WriteLine("Populating Monsters, Subareas, MapSubareas from JSON...");
            using var transaction = connection.BeginTransaction();
            try {
                string basePath = "C:/Jondo/dofus3_data";
                string monstersPath = basePath + "/monsters.json";
                if (System.IO.File.Exists(monstersPath)) {
                    using var fs = new System.IO.FileStream(monstersPath, System.IO.FileMode.Open, System.IO.FileAccess.Read);
                    var doc = JsonDocument.Parse(fs);
                    var refsArr = doc.RootElement.GetProperty("references").GetProperty("RefIds");
                    var insertCmd = connection.CreateCommand();
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = "INSERT INTO Monsters (Id, NameId, Look, Grades) VALUES ($id, $nameId, $look, $grades);";
                    insertCmd.Parameters.Add("$id", SqliteType.Integer);
                    insertCmd.Parameters.Add("$nameId", SqliteType.Integer);
                    insertCmd.Parameters.Add("$look", SqliteType.Text);
                    insertCmd.Parameters.Add("$grades", SqliteType.Text);
                    int countM = 0;
                    for(int i = 0; i < refsArr.GetArrayLength(); i++) {
                        if (!refsArr[i].TryGetProperty("data", out var data)) continue;
                        int monsterId = data.TryGetProperty("id", out var mid) ? mid.GetInt32() : 0;
                        if (monsterId == 0) continue;
                        int nameId = data.TryGetProperty("nameId", out var nid) ? nid.GetInt32() : 0;
                        string look = data.TryGetProperty("look", out var lk) ? lk.GetString() ?? "" : "";
                        string grades = data.TryGetProperty("grades", out var gr) ? gr.GetRawText() : "[]";
                        insertCmd.Parameters["$id"].Value = monsterId;
                        insertCmd.Parameters["$nameId"].Value = nameId;
                        insertCmd.Parameters["$look"].Value = look;
                        insertCmd.Parameters["$grades"].Value = grades;
                        insertCmd.ExecuteNonQuery();
                        countM++;
                    }
                    Console.WriteLine($"[Seeder] Inserted {countM} monsters.");
                }
                string subareasPath = basePath + "/subareas.json";
                if (System.IO.File.Exists(subareasPath)) {
                    using var fs = new System.IO.FileStream(subareasPath, System.IO.FileMode.Open, System.IO.FileAccess.Read);
                    var doc = JsonDocument.Parse(fs);
                    var refsArr = doc.RootElement.GetProperty("references").GetProperty("RefIds");
                    var insertCmd = connection.CreateCommand();
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = "INSERT INTO Subareas (Id, Monsters) VALUES ($id, $monsters);";
                    insertCmd.Parameters.Add("$id", SqliteType.Integer);
                    insertCmd.Parameters.Add("$monsters", SqliteType.Text);
                    for(int i = 0; i < refsArr.GetArrayLength(); i++) {
                        if (!refsArr[i].TryGetProperty("data", out var data)) continue;
                        int subAreaId = data.TryGetProperty("id", out var sid) ? sid.GetInt32() : 0;
                        if (subAreaId == 0) continue;
                        string monsters = data.TryGetProperty("monsters", out var mst) ? mst.GetRawText() : "[]";
                        insertCmd.Parameters["$id"].Value = subAreaId;
                        insertCmd.Parameters["$monsters"].Value = monsters;
                        insertCmd.ExecuteNonQuery();
                    }
                }
                string mapsPath = basePath + "/maps_information.json";
                if (System.IO.File.Exists(mapsPath)) {
                    using var fs = new System.IO.FileStream(mapsPath, System.IO.FileMode.Open, System.IO.FileAccess.Read);
                    var doc = JsonDocument.Parse(fs);
                    var refsArr = doc.RootElement.GetProperty("references").GetProperty("RefIds");
                    var insertCmd = connection.CreateCommand();
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = "INSERT OR REPLACE INTO MapSubareas (MapId, SubAreaId) VALUES ($id, $subid);";
                    insertCmd.Parameters.Add("$id", SqliteType.Integer);
                    insertCmd.Parameters.Add("$subid", SqliteType.Integer);
                    for(int i = 0; i < refsArr.GetArrayLength(); i++) {
                        if (!refsArr[i].TryGetProperty("data", out var data)) continue;
                        long mapId = data.TryGetProperty("id", out var mid) ? mid.GetInt64() : 0;
                        if (mapId == 0) continue;
                        int subAreaId = data.TryGetProperty("subAreaId", out var sid) ? sid.GetInt32() : 0;
                        insertCmd.Parameters["$id"].Value = mapId;
                        insertCmd.Parameters["$subid"].Value = subAreaId;
                        insertCmd.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
            } catch (Exception ex) {
                transaction.Rollback();
                Console.WriteLine("Error populating: " + ex);
            }
        }

        static void PopulateMobs(SqliteConnection connection)
        {
            var clearCmd = connection.CreateCommand();
            clearCmd.CommandText = "DELETE FROM MapMobs;";
            clearCmd.ExecuteNonQuery();

            var monsters = new Dictionary<int, MonsterData>();
            var subareas = new Dictionary<int, List<int>>();
            var mapSubareas = new Dictionary<long, int>();

            var cmdMonsters = connection.CreateCommand();
            cmdMonsters.CommandText = "SELECT Id, NameId, Look, Grades FROM Monsters;";
            using (var reader = cmdMonsters.ExecuteReader()) {
                while (reader.Read()) {
                    var data = new MonsterData { Id = reader.GetInt32(0), NameId = reader.GetInt32(1), Look = reader.GetString(2) };
                    string gradesJson = reader.GetString(3);
                    try {
                        using var doc = JsonDocument.Parse(gradesJson);
                        if (doc.RootElement.ValueKind == JsonValueKind.Array) {
                            foreach(var g in doc.RootElement.EnumerateArray()) {
                                int lvl = g.TryGetProperty("level", out var l) ? l.GetInt32() : 1;
                                data.Grades.Add(new MonsterGrade { Level = lvl });
                            }
                        }
                    } catch {}
                    monsters[data.Id] = data;
                }
            }

            var cmdSubareas = connection.CreateCommand();
            cmdSubareas.CommandText = "SELECT Id, Monsters FROM Subareas;";
            using (var reader = cmdSubareas.ExecuteReader()) {
                while (reader.Read()) {
                    var id = reader.GetInt32(0);
                    var list = new List<int>();
                    string monstersJson = reader.GetString(1);
                    try {
                        using var doc = JsonDocument.Parse(monstersJson);
                        if (doc.RootElement.ValueKind == JsonValueKind.Array) {
                            foreach(var m in doc.RootElement.EnumerateArray()) list.Add(m.GetInt32());
                        } else if (doc.RootElement.TryGetProperty("Array", out var arrProp)) {
                            foreach(var m in arrProp.EnumerateArray()) list.Add(m.GetInt32());
                        }
                    } catch {}
                    subareas[id] = list;
                }
            }

            var cmdMapSubareas = connection.CreateCommand();
            cmdMapSubareas.CommandText = "SELECT MapId, SubAreaId FROM MapSubareas;";
            using (var reader = cmdMapSubareas.ExecuteReader()) {
                while (reader.Read()) mapSubareas[reader.GetInt64(0)] = reader.GetInt32(1);
            }

            long currentMobId = -1000000;
            Random rand = new Random();

            using var transaction = connection.BeginTransaction();
            var insertCmd = connection.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = "INSERT INTO MapMobs (MapId, MobId, CellId, MembersJson) VALUES ($mapId, $mobId, $cellId, $json);";
            insertCmd.Parameters.Add("$mapId", SqliteType.Integer);
            insertCmd.Parameters.Add("$mobId", SqliteType.Integer);
            insertCmd.Parameters.Add("$cellId", SqliteType.Integer);
            insertCmd.Parameters.Add("$json", SqliteType.Text);

            int totalSpawns = 0;
            foreach(var kvp in mapSubareas) {
                long mapId = kvp.Key;
                int subAreaId = kvp.Value;

                if (!subareas.TryGetValue(subAreaId, out var allowedMonsters) || allowedMonsters.Count == 0) continue;
                var validMonsters = allowedMonsters.Where(id => monsters.ContainsKey(id)).ToList();
                if (validMonsters.Count == 0) continue;

                int numMobs = rand.Next(1, 5); // 1 to 4
                for(int i = 0; i < numMobs; i++) {
                    int groupSize = rand.Next(1, 9); // 1 to 8
                    int cellId = rand.Next(50, 450);
                    long mobId = currentMobId--;

                    var members = new List<MobMember>();
                    for(int m = 0; m < groupSize; m++) {
                        int monsterId = validMonsters[rand.Next(validMonsters.Count)];
                        var mData = monsters[monsterId];
                        int gradeIdx = 0;
                        int lvl = 1;
                        if (mData.Grades.Count > 0) {
                            gradeIdx = rand.Next(mData.Grades.Count);
                            lvl = mData.Grades[gradeIdx].Level;
                        }
                        members.Add(new MobMember { id = monsterId, grade = gradeIdx, level = lvl });
                    }

                    insertCmd.Parameters["$mapId"].Value = mapId;
                    insertCmd.Parameters["$mobId"].Value = mobId;
                    insertCmd.Parameters["$cellId"].Value = cellId;
                    insertCmd.Parameters["$json"].Value = JsonSerializer.Serialize(members);
                    insertCmd.ExecuteNonQuery();
                    totalSpawns++;
                }
            }
            transaction.Commit();
            Console.WriteLine($"[Seeder] Inserted {totalSpawns} mobs into DB.");
        }

        static void GiveLevel200Items(SqliteConnection connection, long characterId)
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Id, Data FROM ItemTemplates;";
            var itemsToAdd = new List<(int id, string effects)>();
            using (var reader = cmd.ExecuteReader()) {
                while (reader.Read()) {
                    int id = reader.GetInt32(0);
                    string dataJson = reader.IsDBNull(1) ? "{}" : reader.GetString(1);
                    try {
                        using var doc = JsonDocument.Parse(dataJson);
                        if (doc.RootElement.TryGetProperty("level", out var lvlElem) && lvlElem.ValueKind == JsonValueKind.Number && lvlElem.GetInt32() == 200) {
                            string effects = "[]";
                            if (doc.RootElement.TryGetProperty("possibleEffects", out var effectsElem)) {
                                effects = effectsElem.GetRawText();
                            }
                            itemsToAdd.Add((id, effects));
                        }
                    } catch {}
                }
            }

            var uidCmd = connection.CreateCommand();
            uidCmd.CommandText = "SELECT MAX(Uid) FROM CharacterItems;";
            object maxUidObj = uidCmd.ExecuteScalar();
            long baseUid = maxUidObj != DBNull.Value && maxUidObj != null ? Convert.ToInt64(maxUidObj) + 1 : 500000000;

            using var transaction = connection.BeginTransaction();
            var insertCmd = connection.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = "INSERT INTO CharacterItems (CharacterId, Uid, Gid, Position, Quantity, Effects) VALUES ($charId, $uid, $gid, 63, 1, $effects);";
            insertCmd.Parameters.Add("$charId", SqliteType.Integer);
            insertCmd.Parameters.Add("$uid", SqliteType.Integer);
            insertCmd.Parameters.Add("$gid", SqliteType.Integer);
            insertCmd.Parameters.Add("$effects", SqliteType.Text);

            foreach (var item in itemsToAdd) {
                var checkCmd = connection.CreateCommand();
                checkCmd.Transaction = transaction;
                checkCmd.CommandText = "SELECT COUNT(*) FROM CharacterItems WHERE CharacterId = $cid AND Gid = $cgid;";
                checkCmd.Parameters.AddWithValue("$cid", characterId);
                checkCmd.Parameters.AddWithValue("$cgid", item.id);
                long count = (long)checkCmd.ExecuteScalar();
                if (count == 0) {
                    insertCmd.Parameters["$charId"].Value = characterId;
                    insertCmd.Parameters["$uid"].Value = baseUid++;
                    insertCmd.Parameters["$gid"].Value = item.id;
                    insertCmd.Parameters["$effects"].Value = item.effects;
                    insertCmd.ExecuteNonQuery();
                }
            }
            transaction.Commit();
        }
    }
}
