using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Google.Protobuf;
using Jondo.Unity.Protocol.Messages;

namespace Jondo.Unity.Launcher
{
    public static class DatabaseManager
    {
        private static readonly string AuthConnectionString = Paths.AuthConnectionString;
        public static readonly string WorldConnectionString = Paths.WorldConnectionString;

        public static void Initialize()
        {
            Console.WriteLine("[SQLite] Initializing databases...");
            
            // 1. Initialize auth.db
            using (var authConnection = new SqliteConnection(AuthConnectionString))
            {
                authConnection.Open();
                
                using (var pragmaCmd = authConnection.CreateCommand())
                {
                    pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                    pragmaCmd.ExecuteNonQuery();
                }
                
                var createAccounts = authConnection.CreateCommand();
                createAccounts.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Accounts (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Login TEXT NOT NULL UNIQUE,
                        Password TEXT NOT NULL,
                        Nickname TEXT NOT NULL,
                        GameToken TEXT
                    );
                ";
                createAccounts.ExecuteNonQuery();

                // Seed default account if empty
                var seedAccount = authConnection.CreateCommand();
                seedAccount.CommandText = @"
                    INSERT OR IGNORE INTO Accounts (Id, Login, Password, Nickname)
                    VALUES (188940901, 'jondo@emulator.com', 'password123', 'Jondo');
                ";
                seedAccount.ExecuteNonQuery();
            }

            // 2. Initialize world.db (Auto-extract from world.zip if missing or lacking ItemTemplates)
            string dbPath = Paths.WorldDb;
            bool needsExtraction = !File.Exists(dbPath);
            if (!needsExtraction)
            {
                try
                {
                    using (var checkConn = new SqliteConnection(WorldConnectionString))
                    {
                        checkConn.Open();
                        using var checkCmd = checkConn.CreateCommand();
                        checkCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ItemTemplates';";
                        long hasItemTemplates = Convert.ToInt64(checkCmd.ExecuteScalar() ?? 0L);
                        if (hasItemTemplates == 0) needsExtraction = true;
                        checkConn.Close();
                    }
                    SqliteConnection.ClearAllPools();
                }
                catch { needsExtraction = true; }
            }

            if (needsExtraction)
            {
                string zipPath = Paths.WorldZip;
                if (File.Exists(zipPath))
                {
                    try
                    {
                        SqliteConnection.ClearAllPools();
                        Console.WriteLine("[SQLite] Auto-extracting full world.db from world.zip...");
                        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, Path.GetDirectoryName(Paths.WorldDb)!, true);
                        Console.WriteLine("[SQLite] Successfully extracted world.db from world.zip.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SQLite] Zip extraction skipped (file in use or active): {ex.Message}");
                    }
                }
            }

            using (var worldConnection = new SqliteConnection(WorldConnectionString))
            {
                worldConnection.Open();

                using (var pragmaCmd = worldConnection.CreateCommand())
                {
                    pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                    pragmaCmd.ExecuteNonQuery();
                }

                var createCharacters = worldConnection.CreateCommand();
                createCharacters.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Characters (
                        Id INTEGER PRIMARY KEY,
                        AccountId INTEGER NOT NULL,
                        Name TEXT NOT NULL,
                        Breed INTEGER NOT NULL,
                        Sex INTEGER NOT NULL,
                        Level INTEGER NOT NULL DEFAULT 1,
                        MapId INTEGER NOT NULL DEFAULT 154010884,
                        CellId INTEGER NOT NULL DEFAULT 315,
                        RemainingPoints INTEGER NOT NULL DEFAULT 0,
                        Vitality INTEGER NOT NULL DEFAULT 0,
                        Wisdom INTEGER NOT NULL DEFAULT 0,
                        Strength INTEGER NOT NULL DEFAULT 0,
                        Intelligence INTEGER NOT NULL DEFAULT 0,
                        Chance INTEGER NOT NULL DEFAULT 0,
                        Agility INTEGER NOT NULL DEFAULT 0,
                        Look TEXT NOT NULL,
                        Orientation INTEGER NOT NULL DEFAULT 1,
                        Kamas INTEGER NOT NULL DEFAULT 0
                    );
                ";
                createCharacters.ExecuteNonQuery();

                // Migration: Ensure Orientation column exists
                try
                {
                    var addColCmd = worldConnection.CreateCommand();
                    addColCmd.CommandText = "ALTER TABLE Characters ADD COLUMN Orientation INTEGER NOT NULL DEFAULT 1;";
                    addColCmd.ExecuteNonQuery();
                    Console.WriteLine("[SQLite] Added Orientation column to Characters table.");
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // Column already exists, ignore
                }

                // Migración: la columna de experiencia acumulada del personaje.
                try
                {
                    var addXpCmd = worldConnection.CreateCommand();
                    addXpCmd.CommandText = "ALTER TABLE Characters ADD COLUMN Experience INTEGER NOT NULL DEFAULT 0;";
                    addXpCmd.ExecuteNonQuery();
                    Console.WriteLine("[SQLite] Migración: añadida la columna Experience a Characters.");
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // Ya existe.
                }

                // Migration: Ensure Kamas column exists
                try
                {
                    var addKamasCmd = worldConnection.CreateCommand();
                    addKamasCmd.CommandText = "ALTER TABLE Characters ADD COLUMN Kamas INTEGER NOT NULL DEFAULT 0;";
                    addKamasCmd.ExecuteNonQuery();
                    Console.WriteLine("[SQLite] Migration: Added Kamas column to Characters table.");
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // Column already exists, ignore
                }

                var createItems = worldConnection.CreateCommand();
                createItems.CommandText = @"
                    CREATE TABLE IF NOT EXISTS CharacterItems (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        CharacterId INTEGER NOT NULL,
                        Uid INTEGER NOT NULL,
                        Gid INTEGER NOT NULL,
                        Quantity INTEGER NOT NULL DEFAULT 1,
                        Position INTEGER NOT NULL DEFAULT 63,
                        Effects TEXT
                    );
                ";
                createItems.ExecuteNonQuery();

                // Migration: Ensure Effects column exists in CharacterItems
                try
                {
                    var addColCmd = worldConnection.CreateCommand();
                    addColCmd.CommandText = "ALTER TABLE CharacterItems ADD COLUMN Effects TEXT;";
                    addColCmd.ExecuteNonQuery();
                    Console.WriteLine("[SQLite] Migration: Added Effects column to CharacterItems table.");
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // Column already exists, ignore
                }

                // Seed default character if empty
                var checkChar = worldConnection.CreateCommand();
                checkChar.CommandText = "SELECT COUNT(*) FROM Characters WHERE Id = 13825558;";
                long count = (long)checkChar.ExecuteScalar();
                if (count == 0)
                {
                    var seedChar = worldConnection.CreateCommand();
                    seedChar.CommandText = @"
                        INSERT INTO Characters (
                            Id, AccountId, Name, Breed, Sex, Level, MapId, CellId, 
                            RemainingPoints, Vitality, Wisdom, Strength, Intelligence, Chance, Agility, Look, Kamas
                        ) VALUES (
                            13825558, 188940901, $name, 9, 1, 40, 154010884, 280, 
                            195, 0, 0, 0, 0, 0, 0, $look, 50000
                        );
                    ";
                    seedChar.Parameters.AddWithValue("$name", "[#KEKA-BRON#]");
                    seedChar.Parameters.AddWithValue("$look", "080118032218A28B9B0FCBE5F615A4E1B91992A6C820888CA028F5B7CB342A035BE410420134320220013809");
                    seedChar.ExecuteNonQuery();
                }

                // 3. Initialize NpcSpawns table
                var createNpcSpawns = worldConnection.CreateCommand();
                createNpcSpawns.CommandText = @"
                    CREATE TABLE IF NOT EXISTS NpcSpawns (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        MapId INTEGER NOT NULL,
                        NpcId INTEGER NOT NULL,
                        CellId INTEGER NOT NULL,
                        Orientation INTEGER NOT NULL,
                        BoneId INTEGER NOT NULL,
                        Look TEXT
                    );
                    CREATE TABLE IF NOT EXISTS MapPositions (
                        MapId INTEGER PRIMARY KEY,
                        PosX INTEGER NOT NULL DEFAULT 0,
                        PosY INTEGER NOT NULL DEFAULT 0,
                        SubAreaId INTEGER NOT NULL DEFAULT 1,
                        Outdoor INTEGER NOT NULL DEFAULT 1,
                        Name TEXT
                    );
                    CREATE TABLE IF NOT EXISTS MapScrolls (
                        MapId INTEGER PRIMARY KEY,
                        RightMapId INTEGER NOT NULL DEFAULT 0,
                        BottomMapId INTEGER NOT NULL DEFAULT 0,
                        LeftMapId INTEGER NOT NULL DEFAULT 0,
                        TopMapId INTEGER NOT NULL DEFAULT 0
                    );
                ";
                createNpcSpawns.ExecuteNonQuery();

                // Seed Noken Okuto spawn if empty
                var checkNpc = worldConnection.CreateCommand();
                checkNpc.CommandText = "SELECT COUNT(*) FROM NpcSpawns WHERE NpcId = 2892 AND MapId = 154010883;";
                long npcCount = (long)checkNpc.ExecuteScalar();
                if (npcCount == 0)
                {
                    var seedNpc = worldConnection.CreateCommand();
                    seedNpc.CommandText = @"
                        INSERT INTO NpcSpawns (MapId, NpcId, CellId, Orientation, BoneId, Look)
                        VALUES (154010883, 2892, 329, 3, 231, '{231|||95}');
                    ";
                    seedNpc.ExecuteNonQuery();
                }

                // 4. Initialize Monsters and MapMobs tables
                var createMonsters = worldConnection.CreateCommand();
                createMonsters.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Monsters (
                        Id INTEGER PRIMARY KEY,
                        NameId INTEGER,
                        Look TEXT,
                        Grades TEXT,
                        Spells TEXT DEFAULT '[]'
                    );
                    CREATE TABLE IF NOT EXISTS Subareas (
                        Id INTEGER PRIMARY KEY,
                        Monsters TEXT
                    );
                    CREATE TABLE IF NOT EXISTS MapSubareas (
                        MapId INTEGER PRIMARY KEY,
                        SubAreaId INTEGER
                    );
                    CREATE TABLE IF NOT EXISTS MapMobs (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        MapId INTEGER NOT NULL,
                        MobId INTEGER NOT NULL,
                        CellId INTEGER NOT NULL,
                        MembersJson TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS Spells (
                        Id INTEGER PRIMARY KEY,
                        NameId INTEGER,
                        DescriptionId INTEGER,
                        IconId INTEGER,
                        TypeId INTEGER
                    );
                    CREATE TABLE IF NOT EXISTS SpellLevels (
                        Id INTEGER PRIMARY KEY,
                        SpellId INTEGER,
                        Grade INTEGER,
                        MinPlayerLevel INTEGER,
                        APCost INTEGER,
                        MinRange INTEGER,
                        MaxRange INTEGER,
                        CastInLine INTEGER,
                        MaxCastPerTurn INTEGER,
                        MaxCastPerTarget INTEGER,
                        EffectsJson TEXT
                    );
                    CREATE TABLE IF NOT EXISTS SpellVariants (
                        BreedId INTEGER PRIMARY KEY,
                        SpellIdsJson TEXT
                    );
                ";
                createMonsters.ExecuteNonQuery();

                // 5. Ensure Monsters, Mobs, Spells, and SpellLevels are seeded
                EnsureMobsSeeded(worldConnection);
                EnsureSpellsSeeded(worldConnection);

                if (count == 0)
                {
                    Console.WriteLine("[SQLite] Seeded default character CADERNIS.");
                }
                else
                {
                    // Migration: Update name if it is "[#CADERNIS#]" or "#CADERNIS#"
                    using (var updateCmd = worldConnection.CreateCommand())
                    {
                        updateCmd.CommandText = "UPDATE Characters SET Name = 'CADERNIS' WHERE Name = '[#CADERNIS#]' OR Name = '#CADERNIS#';";
                        int affected = updateCmd.ExecuteNonQuery();
                        if (affected > 0)
                        {
                            Console.WriteLine("[SQLite] Migration: Updated character name to 'CADERNIS'.");
                        }
                    }

                    // Migration: Unstick character from CellId 116
                    using (var updateCellCmd = worldConnection.CreateCommand())
                    {
                        updateCellCmd.CommandText = "UPDATE Characters SET CellId = 320 WHERE CellId = 116 OR CellId <= 0;";
                        int cellAffected = updateCellCmd.ExecuteNonQuery();
                        if (cellAffected > 0)
                        {
                            Console.WriteLine("[SQLite] Migration: Unstuck character, moved to Cell 320.");
                        }
                    }

                    // Migration: Ensure character Breed is 9 (Cra/Ocra)
                    using (var updateBreedCmd = worldConnection.CreateCommand())
                    {
                        updateBreedCmd.CommandText = "UPDATE Characters SET Breed = 9 WHERE Id = 13825558 AND Breed <> 9;";
                        int breedAffected = updateBreedCmd.ExecuteNonQuery();
                        if (breedAffected > 0)
                        {
                            Console.WriteLine("[SQLite] Migration: Updated character Breed to 9 (Ocra).");
                        }
                    }
                }
            }
            // Migration: Add Spells column to Monsters table if missing
            {
                using var wConn = new SqliteConnection(WorldConnectionString);
                wConn.Open();
                var pragmaCmd = wConn.CreateCommand();
                pragmaCmd.CommandText = "PRAGMA table_info(Monsters);";
                bool hasSpells = false;
                using (var pragmaReader = pragmaCmd.ExecuteReader())
                {
                    while (pragmaReader.Read())
                    {
                        if (pragmaReader.GetString(1) == "Spells") { hasSpells = true; break; }
                    }
                }

                if (!hasSpells)
                {
                    var alterCmd = wConn.CreateCommand();
                    alterCmd.CommandText = "ALTER TABLE Monsters ADD COLUMN Spells TEXT DEFAULT '[]';";
                    alterCmd.ExecuteNonQuery();
                    Console.WriteLine("[SQLite] Migration: Added Spells column to Monsters table.");

                    // Re-seed Monsters to populate Spells column
                    var deleteCmd = wConn.CreateCommand();
                    deleteCmd.CommandText = "DELETE FROM Monsters;";
                    deleteCmd.ExecuteNonQuery();
                    PopulateMonstersFromJSON(wConn);
                    Console.WriteLine("[SQLite] Migration: Re-populated Monsters with spell data.");
                }
            }

            Console.WriteLine("[SQLite] Databases initialized successfully.");
        }

        public static void EnsureMobsSeeded(SqliteConnection connection)
        {
            var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM MapMobs;";
            long count = (long)checkCmd.ExecuteScalar();
            if (count > 0) return;

            Console.WriteLine("[DatabaseManager] Auto-seeding Monsters, Subareas, MapSubareas, and MapMobs from JSON...");

            string basePath = Paths.DataDir;
            if (!Directory.Exists(basePath))
            {
                basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dofus3_data");
            }

            if (!Directory.Exists(basePath))
            {
                Console.WriteLine($"[DatabaseManager] Warning: JSON directory not found at {basePath}. Skipping auto-seeding.");
                return;
            }

            var checkMonsters = connection.CreateCommand();
            checkMonsters.CommandText = "SELECT COUNT(*) FROM Monsters;";
            long mCount = (long)checkMonsters.ExecuteScalar();
            if (mCount == 0)
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    string monstersPath = Path.Combine(basePath, "monsters.json");
                    if (File.Exists(monstersPath))
                    {
                        using var fs = new FileStream(monstersPath, FileMode.Open, FileAccess.Read);
                        using var doc = System.Text.Json.JsonDocument.Parse(fs);
                        var refsArr = doc.RootElement.GetProperty("references").GetProperty("RefIds");
                        var insertCmd = connection.CreateCommand();
                        insertCmd.Transaction = transaction;
                        insertCmd.CommandText = "INSERT OR REPLACE INTO Monsters (Id, NameId, Look, Grades) VALUES ($id, $nameId, $look, $grades);";
                        insertCmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
                        insertCmd.Parameters.Add("$nameId", Microsoft.Data.Sqlite.SqliteType.Integer);
                        insertCmd.Parameters.Add("$look", Microsoft.Data.Sqlite.SqliteType.Text);
                        insertCmd.Parameters.Add("$grades", Microsoft.Data.Sqlite.SqliteType.Text);
                        int countM = 0;
                        for (int i = 0; i < refsArr.GetArrayLength(); i++)
                        {
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
                        Console.WriteLine($"[DatabaseManager] Inserted {countM} monsters into DB.");
                    }

                    string subareasPath = Path.Combine(basePath, "subareas.json");
                    if (File.Exists(subareasPath))
                    {
                        using var fs = new FileStream(subareasPath, FileMode.Open, FileAccess.Read);
                        using var doc = System.Text.Json.JsonDocument.Parse(fs);
                        var refsArr = doc.RootElement.GetProperty("references").GetProperty("RefIds");
                        var insertCmd = connection.CreateCommand();
                        insertCmd.Transaction = transaction;
                        insertCmd.CommandText = "INSERT OR REPLACE INTO Subareas (Id, Monsters) VALUES ($id, $monsters);";
                        insertCmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
                        insertCmd.Parameters.Add("$monsters", Microsoft.Data.Sqlite.SqliteType.Text);
                        for (int i = 0; i < refsArr.GetArrayLength(); i++)
                        {
                            if (!refsArr[i].TryGetProperty("data", out var data)) continue;
                            int subAreaId = data.TryGetProperty("id", out var sid) ? sid.GetInt32() : 0;
                            if (subAreaId == 0) continue;
                            string monsters = data.TryGetProperty("monsters", out var mst) ? mst.GetRawText() : "[]";
                            insertCmd.Parameters["$id"].Value = subAreaId;
                            insertCmd.Parameters["$monsters"].Value = monsters;
                            insertCmd.ExecuteNonQuery();
                        }
                    }

                    string mapsPath = Path.Combine(basePath, "maps_information.json");
                    if (File.Exists(mapsPath))
                    {
                        using var fs = new FileStream(mapsPath, FileMode.Open, FileAccess.Read);
                        using var doc = System.Text.Json.JsonDocument.Parse(fs);
                        var refsArr = doc.RootElement.GetProperty("references").GetProperty("RefIds");
                        var insertCmd = connection.CreateCommand();
                        insertCmd.Transaction = transaction;
                        insertCmd.CommandText = "INSERT OR REPLACE INTO MapSubareas (MapId, SubAreaId) VALUES ($id, $subid);";
                        insertCmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
                        insertCmd.Parameters.Add("$subid", Microsoft.Data.Sqlite.SqliteType.Integer);
                        for (int i = 0; i < refsArr.GetArrayLength(); i++)
                        {
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
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    Console.WriteLine("[DatabaseManager] Error seeding JSON data: " + ex.Message);
                }
            }

            PopulateMapMobs(connection);
        }

        private static void PopulateMapMobs(SqliteConnection connection)
        {
            var monsters = new Dictionary<int, Managers.MobSpawnManager.MonsterData>();
            var subareas = new Dictionary<int, List<int>>();
            var mapSubareas = new Dictionary<long, int>();

            var cmdMonsters = connection.CreateCommand();
            cmdMonsters.CommandText = "SELECT Id, NameId, Look, Grades FROM Monsters;";
            using (var reader = cmdMonsters.ExecuteReader())
            {
                while (reader.Read())
                {
                    var data = new Managers.MobSpawnManager.MonsterData
                    {
                        Id = reader.GetInt32(0),
                        NameId = reader.GetInt32(1),
                        Look = reader.GetString(2)
                    };
                    string gradesJson = reader.GetString(3);
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(gradesJson);
                        var root = doc.RootElement;
                        if (root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("Array", out var arrProp))
                        {
                            root = arrProp;
                        }
                        if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var g in root.EnumerateArray())
                            {
                                int lvl = g.TryGetProperty("level", out var l) ? l.GetInt32() : 1;
                                data.Grades.Add(new Managers.MobSpawnManager.MonsterGrade { Level = lvl });
                            }
                        }
                    }
                    catch { }
                    monsters[data.Id] = data;
                }
            }

            var cmdSubareas = connection.CreateCommand();
            cmdSubareas.CommandText = "SELECT Id, Monsters FROM Subareas;";
            using (var reader = cmdSubareas.ExecuteReader())
            {
                while (reader.Read())
                {
                    var id = reader.GetInt32(0);
                    var list = new List<int>();
                    string monstersJson = reader.GetString(1);
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(monstersJson);
                        if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var m in doc.RootElement.EnumerateArray()) list.Add(m.GetInt32());
                        }
                        else if (doc.RootElement.TryGetProperty("Array", out var arrProp))
                        {
                            foreach (var m in arrProp.EnumerateArray()) list.Add(m.GetInt32());
                        }
                    }
                    catch { }
                    subareas[id] = list;
                }
            }

            var cmdMapSubareas = connection.CreateCommand();
            cmdMapSubareas.CommandText = "SELECT MapId, SubAreaId FROM MapSubareas;";
            using (var reader = cmdMapSubareas.ExecuteReader())
            {
                while (reader.Read()) mapSubareas[reader.GetInt64(0)] = reader.GetInt32(1);
            }

            long currentMobId = -1000000;
            Random rand = new Random();

            using var transaction = connection.BeginTransaction();
            var insertCmd = connection.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = "INSERT INTO MapMobs (MapId, MobId, CellId, MembersJson) VALUES ($mapId, $mobId, $cellId, $json);";
            insertCmd.Parameters.Add("$mapId", Microsoft.Data.Sqlite.SqliteType.Integer);
            insertCmd.Parameters.Add("$mobId", Microsoft.Data.Sqlite.SqliteType.Integer);
            insertCmd.Parameters.Add("$cellId", Microsoft.Data.Sqlite.SqliteType.Integer);
            insertCmd.Parameters.Add("$json", Microsoft.Data.Sqlite.SqliteType.Text);

            int totalSpawns = 0;
            foreach (var kvp in mapSubareas)
            {
                long mapId = kvp.Key;
                int subAreaId = kvp.Value;

                if (!subareas.TryGetValue(subAreaId, out var allowedMonsters) || allowedMonsters.Count == 0) continue;
                var validMonsters = allowedMonsters.Where(id => monsters.ContainsKey(id) && id != 494).ToList();
                if (validMonsters.Count == 0) validMonsters = allowedMonsters.Where(id => monsters.ContainsKey(id)).ToList();
                if (validMonsters.Count == 0) continue;

                int numMobs = rand.Next(2, 5); // 2 to 4 groups
                var validCells = Managers.MobSpawnManager.GetInnerWalkableCells(mapId);
                var usedCells = new HashSet<int>();

                for (int i = 0; i < numMobs; i++)
                {
                    int groupSize = rand.Next(1, 9); // 1 to 8 members (official Dofus range)
                    int cellId = validCells[rand.Next(validCells.Count)];
                    while (usedCells.Contains(cellId) && usedCells.Count < validCells.Count)
                    {
                        cellId = validCells[rand.Next(validCells.Count)];
                    }
                    usedCells.Add(cellId);

                    long mobId = currentMobId--;

                    var members = new List<object>();
                    for (int m = 0; m < groupSize; m++)
                    {
                        int monsterId = validMonsters[rand.Next(validMonsters.Count)];
                        var mData = monsters[monsterId];
                        int gradeIdx = 0;
                        int lvl = 1;
                        if (mData.Grades.Count > 0)
                        {
                            gradeIdx = rand.Next(mData.Grades.Count);
                            lvl = mData.Grades[gradeIdx].Level;
                        }
                        members.Add(new { id = monsterId, grade = gradeIdx, level = lvl });
                    }

                    insertCmd.Parameters["$mapId"].Value = mapId;
                    insertCmd.Parameters["$mobId"].Value = mobId;
                    insertCmd.Parameters["$cellId"].Value = cellId;
                    insertCmd.Parameters["$json"].Value = System.Text.Json.JsonSerializer.Serialize(members);
                    insertCmd.ExecuteNonQuery();
                    totalSpawns++;
                }
            }
            transaction.Commit();
            Console.WriteLine($"[DatabaseManager] Successfully auto-seeded {totalSpawns} mobs into MapMobs table.");
        }

        // --- Auth Operations ---

        public static void SetGameToken(long accountId, string token)
        {
            using var connection = new SqliteConnection(AuthConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE Accounts
                SET GameToken = $token
                WHERE Id = $id;
            ";
            command.Parameters.AddWithValue("$token", token);
            command.Parameters.AddWithValue("$id", accountId);
            command.ExecuteNonQuery();
        }

        public static bool ValidateGameToken(string token)
        {
            using var connection = new SqliteConnection(AuthConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT COUNT(*)
                FROM Accounts
                WHERE GameToken = $token;
            ";
            command.Parameters.AddWithValue("$token", token);
            return (long)command.ExecuteScalar() > 0;
        }

        public static long GetAccountIdByToken(string token)
        {
            using var connection = new SqliteConnection(AuthConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT Id FROM Accounts WHERE GameToken = $token;";
            command.Parameters.AddWithValue("$token", token);
            var result = command.ExecuteScalar();
            return result != null ? (long)result : 0;
        }

        // --- Character Operations ---

        public class DbCharacter
        {
            public long Id { get; set; }
            public string Name { get; set; }
            public int Breed { get; set; }
            public int Sex { get; set; }
            public int Level { get; set; }
            public string LookHex { get; set; }
        }

        public static List<DbCharacter> GetCharactersByAccountId(long accountId)
        {
            var list = new List<DbCharacter>();
            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, Name, Breed, Sex, Level, Look FROM Characters WHERE AccountId = $accId;";
            command.Parameters.AddWithValue("$accId", accountId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new DbCharacter
                {
                    Id = reader.GetInt64(0),
                    Name = reader.GetString(1),
                    Breed = reader.GetInt32(2),
                    Sex = reader.GetInt32(3),
                    Level = reader.GetInt32(4),
                    LookHex = reader.GetString(5)
                });
            }
            return list;
        }

        public static bool LoadCharacter(long characterId)
        {
            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT Name, Level, MapId, CellId, RemainingPoints, Vitality, Wisdom, Strength, Intelligence, Chance, Agility, Look, Breed, Sex, Orientation, Kamas, Experience
                FROM Characters
                WHERE Id = $charId;
            ";
            command.Parameters.AddWithValue("$charId", characterId);

            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                GameState.CharacterId = characterId;
                GameState.CharacterName = reader.GetString(0);
                GameState.CharacterLevel = reader.GetInt32(1);
                // Load actual position from the database
                GameState.MapId = reader.GetInt64(2);
                GameState.CellId = reader.GetInt32(3);
                GameState.Orientation = reader.IsDBNull(14) ? 1 : reader.GetInt32(14);
                GameState.Kamas = reader.IsDBNull(15) ? 0 : reader.GetInt64(15);

                // Si el personaje viene de antes de que existiera la columna, se le da la
                // experiencia mínima que corresponde a su nivel para que la barra no salga vacía.
                long xpGuardada = reader.IsDBNull(16) ? 0 : reader.GetInt64(16);
                long sueloNivel = ExperienceTable.LevelFloor(GameState.CharacterLevel);
                GameState.Experience = Math.Max(xpGuardada, sueloNivel);
                GameState.CharacterRemainingPoints = reader.GetInt32(4);
                GameState.StatVitality = reader.GetInt32(5);
                GameState.StatWisdom = reader.GetInt32(6);
                GameState.StatStrength = reader.GetInt32(7);
                GameState.StatIntelligence = reader.GetInt32(8);
                GameState.StatChance = reader.GetInt32(9);
                GameState.StatAgility = reader.GetInt32(10);
                GameState.Breed = reader.GetInt32(12);
                GameState.Sex = reader.GetInt32(13);
                
                string lookHex = reader.GetString(11);
                byte[] lookBytes = ConvertHexStringToByteArray(lookHex);
                GameState.LookBytes = lookBytes;
                
                // Reconstruct PlayerActorDetails (detailsMsg with look and humanoid name)
                // detailsMsg has: Field 1 (Look), Field 2 (HumanoidMsg)
                // HumanoidMsg has: Field 2 (HumanInformationsMsg)
                // HumanInformationsMsg has: Field 3 (Name)
                GameState.PlayerActorDetails = ReconstructActorDetails(lookBytes, GameState.CharacterName);
                
                Console.WriteLine($"[SQLite] Successfully loaded character: {GameState.CharacterName} (Level {GameState.CharacterLevel})");
                return true;
            }
            return false;
        }

        public static void SaveCurrentCharacter()
        {
            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE Characters
                SET MapId = $mapId, CellId = $cellId, Orientation = $orientation,
                    RemainingPoints = $pts, Vitality = $vit, Wisdom = $wis,
                    Strength = $str, Intelligence = $int, Chance = $cha, Agility = $agi,
                    Level = $lvl, Kamas = $kamas, Experience = $xp
                WHERE Id = $charId;
            ";
            command.Parameters.AddWithValue("$charId", GameState.CharacterId);
            command.Parameters.AddWithValue("$mapId", GameState.MapId);
            command.Parameters.AddWithValue("$cellId", GameState.CellId);
            command.Parameters.AddWithValue("$orientation", GameState.Orientation);
            command.Parameters.AddWithValue("$pts", GameState.CharacterRemainingPoints);
            command.Parameters.AddWithValue("$vit", GameState.StatVitality);
            command.Parameters.AddWithValue("$wis", GameState.StatWisdom);
            command.Parameters.AddWithValue("$str", GameState.StatStrength);
            command.Parameters.AddWithValue("$int", GameState.StatIntelligence);
            command.Parameters.AddWithValue("$cha", GameState.StatChance);
            command.Parameters.AddWithValue("$agi", GameState.StatAgility);
            command.Parameters.AddWithValue("$lvl", GameState.CharacterLevel);
            command.Parameters.AddWithValue("$kamas", GameState.Kamas);
            command.Parameters.AddWithValue("$xp", GameState.Experience);
            command.ExecuteNonQuery();
        }

        public static void SaveCharacterLook(long characterId, byte[] lookBytes)
        {
            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = "UPDATE Characters SET Look = $look WHERE Id = $id;";
            command.Parameters.AddWithValue("$look", BitConverter.ToString(lookBytes).Replace("-", ""));
            command.Parameters.AddWithValue("$id", characterId);
            command.ExecuteNonQuery();
        }

        // --- Inventory Operations ---

        public static List<PlayerItem> LoadInventory(long characterId)
        {
            var list = new List<PlayerItem>();
            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT Uid, Gid, Quantity, Position, Effects FROM CharacterItems WHERE CharacterId = $charId;";
            command.Parameters.AddWithValue("$charId", characterId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var item = new PlayerItem
                {
                    Uid = reader.GetInt64(0),
                    ItemId = reader.GetInt32(1),
                    Quantity = reader.GetInt32(2),
                    Position = reader.GetInt32(3)
                };

                string jsonEffects = reader.IsDBNull(4) ? "" : reader.GetString(4);
                if (!string.IsNullOrEmpty(jsonEffects))
                {
                    try
                    {
                        var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, int>>(jsonEffects);
                        if (dict != null)
                        {
                            foreach (var kvp in dict)
                            {
                                item.Effects[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                    catch (Exception) { }
                }

                list.Add(item);
            }
            return list;
        }

        public static void SaveInventoryItem(long characterId, PlayerItem item)
        {
            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            string jsonEffects = System.Text.Json.JsonSerializer.Serialize(item.Effects);

            var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO CharacterItems (CharacterId, Uid, Gid, Quantity, Position, Effects)
                VALUES ($charId, $uid, $gid, $qty, $pos, $effects)
                ON CONFLICT(Uid) DO UPDATE SET
                    Gid = $gid,
                    Quantity = $qty,
                    Position = $pos,
                    Effects = $effects;
            ";
            command.Parameters.AddWithValue("$charId", characterId);
            command.Parameters.AddWithValue("$uid", item.Uid);
            command.Parameters.AddWithValue("$gid", item.ItemId);
            command.Parameters.AddWithValue("$qty", item.Quantity);
            command.Parameters.AddWithValue("$pos", item.Position);
            command.Parameters.AddWithValue("$effects", jsonEffects);
            command.ExecuteNonQuery();
        }

        public static void SaveItemPosition(long uid, int position)
        {
            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = "UPDATE CharacterItems SET Position = $pos WHERE Uid = $uid;";
            command.Parameters.AddWithValue("$pos", position);
            command.Parameters.AddWithValue("$uid", uid);
            command.ExecuteNonQuery();
        }

        /// <summary>Reads an item template's realWeight (pods) from the ItemTemplates Data JSON.</summary>
        public static int GetItemRealWeight(int gid)
        {
            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT Data FROM ItemTemplates WHERE Id = $gid;";
            command.Parameters.AddWithValue("$gid", gid);
            if (command.ExecuteScalar() is not string data || string.IsNullOrEmpty(data))
                return 0;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("realWeight", out var weight))
                    return weight.TryGetInt32(out int w) ? w : (int)weight.GetDouble();
            }
            catch (Exception) { }
            return 0;
        }

        public static void ClearInventory(long characterId)
        {
            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM CharacterItems WHERE CharacterId = $charId;";
            command.Parameters.AddWithValue("$charId", characterId);
            command.ExecuteNonQuery();
        }

        public static void SeedInventory(long characterId, List<PlayerItem> items)
        {
            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            using var transaction = connection.BeginTransaction();
            try
            {
                // Create UNIQUE index on Uid to support INSERT ... ON CONFLICT
                var createIndex = connection.CreateCommand();
                createIndex.Transaction = transaction;
                createIndex.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_items_uid ON CharacterItems(Uid);";
                createIndex.ExecuteNonQuery();

                foreach (var item in items)
                {
                    string jsonEffects = System.Text.Json.JsonSerializer.Serialize(item.Effects);
                    var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = @"
                        INSERT OR REPLACE INTO CharacterItems (CharacterId, Uid, Gid, Quantity, Position, Effects)
                        VALUES ($charId, $uid, $gid, $qty, $pos, $effects);
                    ";
                    command.Parameters.AddWithValue("$charId", characterId);
                    command.Parameters.AddWithValue("$uid", item.Uid);
                    command.Parameters.AddWithValue("$gid", item.ItemId);
                    command.Parameters.AddWithValue("$qty", item.Quantity);
                    command.Parameters.AddWithValue("$pos", item.Position);
                    command.Parameters.AddWithValue("$effects", jsonEffects);
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
                Console.WriteLine($"[SQLite] Successfully seeded {items.Count} items into database for Character {characterId}.");
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                Console.WriteLine($"[-] Error seeding inventory: {ex.Message}");
            }
        }

        // --- Helpers ---

        private static byte[] ConvertHexStringToByteArray(string hex)
        {
            hex = hex.Replace("-", "");
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }

        public static void GiveAllLevel200Items(long characterId)
        {
            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            // First check if already given
            var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM CharacterItems WHERE CharacterId = $id AND ItemGid = 21081;"; // Examples
            checkCmd.Parameters.AddWithValue("$id", characterId);
            long count = (long)checkCmd.ExecuteScalar();
            if (count > 0) return;

            Console.WriteLine($"[SQLite] Giving all level 200 items to Character {characterId}...");

            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Id, PossibleEffects FROM ItemTemplates WHERE Level = 200;";
            var itemsToAdd = new List<(int id, string effects)>();

            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    int id = reader.GetInt32(0);
                    string effects = reader.IsDBNull(1) ? "[]" : reader.GetString(1);
                    itemsToAdd.Add((id, effects));
                }
            }

            using var transaction = connection.BeginTransaction();
            try
            {
                var insertCmd = connection.CreateCommand();
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = @"
                    INSERT INTO CharacterItems (CharacterId, ItemGid, Position, Quantity, Effects)
                    VALUES ($charId, $gid, 63, 1, $effects);
                ";
                insertCmd.Parameters.Add("$charId", SqliteType.Integer);
                insertCmd.Parameters.Add("$gid", SqliteType.Integer);
                insertCmd.Parameters.Add("$effects", SqliteType.Text);

                foreach (var item in itemsToAdd)
                {
                    insertCmd.Parameters["$charId"].Value = characterId;
                    insertCmd.Parameters["$gid"].Value = item.id;
                    insertCmd.Parameters["$effects"].Value = item.effects;
                    insertCmd.ExecuteNonQuery();
                }

                transaction.Commit();
                Console.WriteLine($"[SQLite] Successfully added {itemsToAdd.Count} level 200 items.");
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                Console.WriteLine($"[SQLite] Error adding level 200 items: {ex.Message}");
            }
        }

        public static byte[] ReconstructActorDetails(byte[] lookBytes, string name)
        {
            try
            {
                var statsMsg = new Network.ProtoMessage();
                
                // Field 1: breed & sex wrapper
                var breedSexMsg = new Network.ProtoMessage();
                breedSexMsg.Fields.Add(new Network.ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = GameState.Breed > 0 ? GameState.Breed : 8 });
                breedSexMsg.Fields.Add(new Network.ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = GameState.Sex });
                statsMsg.Fields.Add(new Network.ProtoField { FieldNumber = 1, WireType = 2, BytesValue = breedSexMsg.ToByteArray() });
                
                // Field 2: Level
                statsMsg.Fields.Add(new Network.ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = GameState.CharacterLevel > 0 ? GameState.CharacterLevel : 2 });
                
                // Field 4: AccountId (using default 188940901L)
                statsMsg.Fields.Add(new Network.ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 188940901L });

                // Field 5: alignment
                var alignMsg = new Network.ProtoMessage();
                alignMsg.Fields.Add(new Network.ProtoField { FieldNumber = 6, WireType = 0, VarIntValue = 1 });
                statsMsg.Fields.Add(new Network.ProtoField { FieldNumber = 5, WireType = 2, BytesValue = alignMsg.ToByteArray() });

                // Field 7: constant 1
                statsMsg.Fields.Add(new Network.ProtoField { FieldNumber = 7, WireType = 0, VarIntValue = 1 });

                // Build lgk (Player Desc)
                var lgkMsg = new Network.ProtoMessage();
                lgkMsg.Fields.Add(new Network.ProtoField { FieldNumber = 2, WireType = 2, BytesValue = statsMsg.ToByteArray() });
                lgkMsg.Fields.Add(new Network.ProtoField { FieldNumber = 3, WireType = 2, BytesValue = System.Text.Encoding.UTF8.GetBytes(name) });

                // Build humanoidInfo wrapper (HumanInformations)
                var humanoidInfo = new Network.ProtoMessage();
                humanoidInfo.Fields.Add(new Network.ProtoField { FieldNumber = 2, WireType = 2, BytesValue = lgkMsg.ToByteArray() });

                // Build detailsMsg (lgx)
                var detailsMsg = new Network.ProtoMessage();
                if (lookBytes != null && lookBytes.Length > 0)
                {
                    detailsMsg.Fields.Add(new Network.ProtoField { FieldNumber = 1, WireType = 2, BytesValue = lookBytes });
                }
                detailsMsg.Fields.Add(new Network.ProtoField { FieldNumber = 2, WireType = 2, BytesValue = humanoidInfo.ToByteArray() });

                return detailsMsg.ToByteArray();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[-] Error in ReconstructActorDetails: {ex.Message}");
                return Array.Empty<byte>();
            }
        }

        public class NpcSpawn
        {
            public int Id { get; set; }
            public long MapId { get; set; }
            public int NpcId { get; set; }
            public int CellId { get; set; }
            public int Orientation { get; set; }
            public int BoneId { get; set; }
            public string Look { get; set; } = "";
        }

        public static List<NpcSpawn> GetNpcSpawnsForMap(long mapId)
        {
            var spawns = new List<NpcSpawn>();
            try
            {
                using (var connection = new SqliteConnection(WorldConnectionString))
                {
                    connection.Open();
                    var command = connection.CreateCommand();
                    command.CommandText = "SELECT Id, MapId, NpcId, CellId, Orientation, BoneId, Look FROM NpcSpawns WHERE MapId = $mapId;";
                    command.Parameters.AddWithValue("$mapId", mapId);
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            spawns.Add(new NpcSpawn
                            {
                                Id = reader.GetInt32(0),
                                MapId = reader.GetInt64(1),
                                NpcId = reader.GetInt32(2),
                                CellId = reader.GetInt32(3),
                                Orientation = reader.GetInt32(4),
                                BoneId = reader.GetInt32(5),
                                Look = reader.IsDBNull(6) ? "" : reader.GetString(6)
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[-] Error fetching NPC spawns for map {mapId}: {ex.Message}");
            }
            return spawns;
        }

        public static string GetNpcTemplateLook(int npcId)
        {
            try
            {
                using (var connection = new SqliteConnection(WorldConnectionString))
                {
                    connection.Open();
                    var command = connection.CreateCommand();
                    command.CommandText = "SELECT Look FROM NpcTemplates WHERE Id = $npcId;";
                    command.Parameters.AddWithValue("$npcId", npcId);
                    var val = command.ExecuteScalar();
                    if (val != null && val != DBNull.Value)
                    {
                        return val.ToString() ?? "";
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[-] Error fetching NPC template look for NPC {npcId}: {ex.Message}");
            }
            return "";
        }

        public static List<long> GetItemTemplatePossibleEffects(int itemId)
        {
            var rids = new List<long>();
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT Data FROM ItemTemplates WHERE Id = $itemId;";
                command.Parameters.AddWithValue("$itemId", itemId);
                var data = command.ExecuteScalar();
                if (data != null && data != DBNull.Value)
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(data.ToString()!);
                    if (doc.RootElement.TryGetProperty("possibleEffects", out var possibleEffects))
                    {
                        if (possibleEffects.TryGetProperty("Array", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var element in arr.EnumerateArray())
                            {
                                if (element.TryGetProperty("rid", out var ridProp))
                                {
                                    rids.Add(ridProp.GetInt64());
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[-] Error parsing ItemTemplate {itemId} for possibleEffects: {ex.Message}");
            }
            return rids;
        }

        public class ItemEffectData
        {
            public int EffectId { get; set; }
            public int DiceNum { get; set; }
            public int DiceSide { get; set; }
            public int Value { get; set; }
        }

        public static List<ItemEffectData> GetItemEffectsData(List<long> rids)
        {
            var results = new List<ItemEffectData>();
            if (rids == null || rids.Count == 0) return results;

            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                
                var parameters = string.Join(",", rids.Select((_, i) => $"$p{i}"));
                var command = connection.CreateCommand();
                command.CommandText = $"SELECT EffectId, DiceNum, DiceSide, Value FROM ItemEffects WHERE Rid IN ({parameters})";
                
                for (int i = 0; i < rids.Count; i++)
                {
                    command.Parameters.AddWithValue($"$p{i}", rids[i]);
                }

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new ItemEffectData
                    {
                        EffectId = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                        DiceNum = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                        DiceSide = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                        Value = reader.IsDBNull(3) ? 0 : reader.GetInt32(3)
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[-] Error fetching ItemEffectsData: {ex.Message}");
            }
            return results;
        }
        private static string? FindDataFile(string filename)
        {
            string[] candidates = new string[]
            {
                Path.Combine(Paths.DataDir, filename),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "dofus3_data", filename),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dofus3_data", filename),
                Path.Combine(@"..\dofus3_data", filename),
                filename
            };
            foreach (var path in candidates)
            {
                try { if (File.Exists(path)) return path; } catch { }
            }
            return null;
        }

        public static void PopulateMonstersFromJSON(SqliteConnection connection)
        {
            var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM Monsters;";
            long count = (long)checkCmd.ExecuteScalar();
            if (count > 0) return; // Already populated

            Console.WriteLine("[SQLite] Populating Monsters, Subareas, and MapSubareas from JSON. This may take a moment...");
            
            using var transaction = connection.BeginTransaction();
            try
            {
                // Monsters
                string? monstersPath = FindDataFile("monsters.json");
                if (!string.IsNullOrEmpty(monstersPath) && File.Exists(monstersPath))
                {
                    using var fs = new FileStream(monstersPath, FileMode.Open, FileAccess.Read);
                    var doc = System.Text.Json.JsonDocument.Parse(fs);

                    var insertCmd = connection.CreateCommand();
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = "INSERT OR REPLACE INTO Monsters (Id, NameId, Look, Grades, Spells) VALUES ($id, $nameId, $look, $grades, $spells);";
                    insertCmd.Parameters.Add("$id", SqliteType.Integer);
                    insertCmd.Parameters.Add("$nameId", SqliteType.Integer);
                    insertCmd.Parameters.Add("$look", SqliteType.Text);
                    insertCmd.Parameters.Add("$grades", SqliteType.Text);
                    insertCmd.Parameters.Add("$spells", SqliteType.Text);

                    if (doc.RootElement.TryGetProperty("references", out var refsObj) && refsObj.TryGetProperty("RefIds", out var refIdsArr))
                    {
                        foreach (var item in refIdsArr.EnumerateArray())
                        {
                            if (!item.TryGetProperty("data", out var data)) continue;
                            int monsterId = data.TryGetProperty("id", out var mid) ? mid.GetInt32() : 0;
                            if (monsterId <= 0) continue;

                            int nameId = data.TryGetProperty("nameId", out var nid) ? nid.GetInt32() : 0;
                            string look = data.TryGetProperty("look", out var lk) ? lk.GetString() : "";
                            string grades = data.TryGetProperty("grades", out var gr) ? gr.GetRawText() : "[]";

                            string spellsJson = "[]";
                            if (data.TryGetProperty("spells", out var spellsProp))
                            {
                                if (spellsProp.ValueKind == System.Text.Json.JsonValueKind.Object && spellsProp.TryGetProperty("Array", out var spellArr))
                                    spellsJson = spellArr.GetRawText();
                                else if (spellsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                                    spellsJson = spellsProp.GetRawText();
                            }

                            insertCmd.Parameters["$id"].Value = monsterId;
                            insertCmd.Parameters["$nameId"].Value = nameId;
                            insertCmd.Parameters["$look"].Value = look ?? "";
                            insertCmd.Parameters["$grades"].Value = grades;
                            insertCmd.Parameters["$spells"].Value = spellsJson;
                            insertCmd.ExecuteNonQuery();
                        }
                    }
                    else if (doc.RootElement.TryGetProperty("objectsById", out var objById))
                    {
                        var mValuesArr = objById.GetProperty("m_values").GetProperty("Array");
                        var mKeysArr = objById.GetProperty("m_keys").GetProperty("Array");

                        for (int i = 0; i < mKeysArr.GetArrayLength(); i++)
                        {
                            var monsterId = mKeysArr[i].GetInt32();
                            var data = mValuesArr[i].TryGetProperty("data", out var d) ? d : mValuesArr[i];
                            int nameId = data.TryGetProperty("nameId", out var nid) ? nid.GetInt32() : 0;
                            string look = data.TryGetProperty("look", out var lk) ? lk.GetString() : "";
                            string grades = data.TryGetProperty("grades", out var gr) ? gr.GetRawText() : "[]";

                            string spellsJson = "[]";
                            if (data.TryGetProperty("spells", out var spellsProp))
                            {
                                if (spellsProp.ValueKind == System.Text.Json.JsonValueKind.Object && spellsProp.TryGetProperty("Array", out var spellArr))
                                    spellsJson = spellArr.GetRawText();
                                else if (spellsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                                    spellsJson = spellsProp.GetRawText();
                            }

                            insertCmd.Parameters["$id"].Value = monsterId;
                            insertCmd.Parameters["$nameId"].Value = nameId;
                            insertCmd.Parameters["$look"].Value = look ?? "";
                            insertCmd.Parameters["$grades"].Value = grades;
                            insertCmd.Parameters["$spells"].Value = spellsJson;
                            insertCmd.ExecuteNonQuery();
                        }
                    }
                }

                // Subareas
                string? subareasPath = FindDataFile("subareas.json");
                if (!string.IsNullOrEmpty(subareasPath) && File.Exists(subareasPath))
                {
                    using var fs = new FileStream(subareasPath, FileMode.Open, FileAccess.Read);
                    var doc = System.Text.Json.JsonDocument.Parse(fs);
                    var mValuesArr = doc.RootElement.GetProperty("objectsById").GetProperty("m_values").GetProperty("Array");
                    var mKeysArr = doc.RootElement.GetProperty("objectsById").GetProperty("m_keys").GetProperty("Array");
                    
                    var insertCmd = connection.CreateCommand();
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = "INSERT OR REPLACE INTO Subareas (Id, Monsters) VALUES ($id, $monsters);";
                    insertCmd.Parameters.Add("$id", SqliteType.Integer);
                    insertCmd.Parameters.Add("$monsters", SqliteType.Text);

                    for(int i = 0; i < mKeysArr.GetArrayLength(); i++)
                    {
                        var subAreaId = mKeysArr[i].GetInt32();
                        var data = mValuesArr[i].GetProperty("data");
                        string monsters = data.TryGetProperty("monsters", out var mst) ? mst.GetRawText() : "[]";

                        insertCmd.Parameters["$id"].Value = subAreaId;
                        insertCmd.Parameters["$monsters"].Value = monsters;
                        insertCmd.ExecuteNonQuery();
                    }
                }

                // MapSubareas
                string? mapsPath = FindDataFile("maps_information.json");
                if (!string.IsNullOrEmpty(mapsPath) && File.Exists(mapsPath))
                {
                    using var fs = new FileStream(mapsPath, FileMode.Open, FileAccess.Read);
                    var doc = System.Text.Json.JsonDocument.Parse(fs);
                    var mValuesArr = doc.RootElement.GetProperty("objectsById").GetProperty("m_values").GetProperty("Array");
                    var mKeysArr = doc.RootElement.GetProperty("objectsById").GetProperty("m_keys").GetProperty("Array");
                    
                    var insertCmd = connection.CreateCommand();
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = "INSERT OR REPLACE INTO MapSubareas (MapId, SubAreaId) VALUES ($id, $subid);";
                    insertCmd.Parameters.Add("$id", SqliteType.Integer);
                    insertCmd.Parameters.Add("$subid", SqliteType.Integer);

                    for(int i = 0; i < mKeysArr.GetArrayLength(); i++)
                    {
                        long mapId = mKeysArr[i].GetInt64();
                        var data = mValuesArr[i].GetProperty("data");
                        int subAreaId = data.TryGetProperty("subAreaId", out var sid) ? sid.GetInt32() : 0;

                        insertCmd.Parameters["$id"].Value = mapId;
                        insertCmd.Parameters["$subid"].Value = subAreaId;
                        insertCmd.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
                Console.WriteLine("[SQLite] Successfully populated JSON data.");
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                Console.WriteLine($"[-] Error populating from JSON: {ex.Message}");
            }
        }

        public static void EnsureSpellsSeeded(SqliteConnection connection)
        {
            var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM Spells;";
            long count = (long)checkCmd.ExecuteScalar();
            if (count > 0) return;

            Console.WriteLine("[DatabaseManager] Auto-seeding Spells, SpellLevels, and SpellVariants from JSON...");

            string basePath = Paths.DataDir;
            if (!Directory.Exists(basePath))
            {
                basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dofus3_data");
            }

            if (!Directory.Exists(basePath)) return;

            // Seed Spells
            string spellsPath = Path.Combine(basePath, "spells.json");
            if (File.Exists(spellsPath))
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(spellsPath));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("references", out var refs) && refs.TryGetProperty("RefIds", out var refIds))
                    {
                        var insertCmd = connection.CreateCommand();
                        insertCmd.Transaction = transaction;
                        insertCmd.CommandText = "INSERT OR REPLACE INTO Spells (Id, NameId, DescriptionId, IconId, TypeId) VALUES ($id, $nid, $did, $iid, $tid);";
                        insertCmd.Parameters.Add("$id", SqliteType.Integer);
                        insertCmd.Parameters.Add("$nid", SqliteType.Integer);
                        insertCmd.Parameters.Add("$did", SqliteType.Integer);
                        insertCmd.Parameters.Add("$iid", SqliteType.Integer);
                        insertCmd.Parameters.Add("$tid", SqliteType.Integer);

                        foreach (var item in refIds.EnumerateArray())
                        {
                            if (!item.TryGetProperty("data", out var d)) continue;
                            int id = d.TryGetProperty("id", out var sid) ? sid.GetInt32() : 0;
                            if (id <= 0) continue;
                            insertCmd.Parameters["$id"].Value = id;
                            insertCmd.Parameters["$nid"].Value = d.TryGetProperty("nameId", out var nid) ? nid.GetInt32() : 0;
                            insertCmd.Parameters["$did"].Value = d.TryGetProperty("descriptionId", out var did) ? did.GetInt32() : 0;
                            insertCmd.Parameters["$iid"].Value = d.TryGetProperty("iconId", out var iid) ? iid.GetInt32() : 0;
                            insertCmd.Parameters["$tid"].Value = d.TryGetProperty("typeId", out var tid) ? tid.GetInt32() : 0;
                            insertCmd.ExecuteNonQuery();
                        }
                    }
                    transaction.Commit();
                }
                catch { transaction.Rollback(); }
            }

            // Seed SpellLevels
            string slPath = Path.Combine(basePath, "spell_levels.json");
            if (File.Exists(slPath))
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(slPath));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("references", out var refs) && refs.TryGetProperty("RefIds", out var refIds))
                    {
                        var insertCmd = connection.CreateCommand();
                        insertCmd.Transaction = transaction;
                        insertCmd.CommandText = "INSERT OR REPLACE INTO SpellLevels (Id, SpellId, Grade, MinPlayerLevel, APCost, MinRange, MaxRange, CastInLine, MaxCastPerTurn, MaxCastPerTarget, EffectsJson) VALUES ($id, $sid, $grade, $mpl, $ap, $minr, $maxr, $cil, $mcpt, $mcptg, $fx);";
                        insertCmd.Parameters.Add("$id", SqliteType.Integer);
                        insertCmd.Parameters.Add("$sid", SqliteType.Integer);
                        insertCmd.Parameters.Add("$grade", SqliteType.Integer);
                        insertCmd.Parameters.Add("$mpl", SqliteType.Integer);
                        insertCmd.Parameters.Add("$ap", SqliteType.Integer);
                        insertCmd.Parameters.Add("$minr", SqliteType.Integer);
                        insertCmd.Parameters.Add("$maxr", SqliteType.Integer);
                        insertCmd.Parameters.Add("$cil", SqliteType.Integer);
                        insertCmd.Parameters.Add("$mcpt", SqliteType.Integer);
                        insertCmd.Parameters.Add("$mcptg", SqliteType.Integer);
                        insertCmd.Parameters.Add("$fx", SqliteType.Text);

                        foreach (var item in refIds.EnumerateArray())
                        {
                            if (!item.TryGetProperty("data", out var d)) continue;
                            int id = d.TryGetProperty("id", out var slid) ? slid.GetInt32() : 0;
                            if (id <= 0) continue;

                            string fxJson = "[]";
                            if (d.TryGetProperty("effects", out var fxObj) && fxObj.TryGetProperty("Array", out var fxArr))
                            {
                                fxJson = fxArr.GetRawText();
                            }

                            insertCmd.Parameters["$id"].Value = id;
                            insertCmd.Parameters["$sid"].Value = d.TryGetProperty("spellId", out var sid) ? sid.GetInt32() : 0;
                            insertCmd.Parameters["$grade"].Value = d.TryGetProperty("grade", out var g) ? g.GetInt32() : 1;
                            insertCmd.Parameters["$mpl"].Value = d.TryGetProperty("minPlayerLevel", out var mpl) ? mpl.GetInt32() : 0;
                            insertCmd.Parameters["$ap"].Value = d.TryGetProperty("apCost", out var ap) ? ap.GetInt32() : 3;
                            insertCmd.Parameters["$minr"].Value = d.TryGetProperty("minRange", out var minr) ? minr.GetInt32() : 0;
                            insertCmd.Parameters["$maxr"].Value = d.TryGetProperty("range", out var maxr) ? maxr.GetInt32() : 1;
                            insertCmd.Parameters["$cil"].Value = d.TryGetProperty("castInLine", out var cil) && cil.GetBoolean() ? 1 : 0;
                            insertCmd.Parameters["$mcpt"].Value = d.TryGetProperty("maxCastPerTurn", out var mcpt) ? mcpt.GetInt32() : 0;
                            insertCmd.Parameters["$mcptg"].Value = d.TryGetProperty("maxCastPerTarget", out var mcptg) ? mcptg.GetInt32() : 0;
                            insertCmd.Parameters["$fx"].Value = fxJson;
                            insertCmd.ExecuteNonQuery();
                        }
                    }
                    transaction.Commit();
                }
                catch { transaction.Rollback(); }
            }

            // Seed SpellVariants
            string svPath = Path.Combine(basePath, "spell_variants.json");
            if (File.Exists(svPath))
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(svPath));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("references", out var refs) && refs.TryGetProperty("RefIds", out var refIds))
                    {
                        var breedMap = new Dictionary<int, List<int>>();
                        foreach (var item in refIds.EnumerateArray())
                        {
                            if (!item.TryGetProperty("data", out var d)) continue;
                            int bid = d.TryGetProperty("breedId", out var b) ? b.GetInt32() : 0;
                            if (bid <= 0) continue;
                            if (!breedMap.ContainsKey(bid)) breedMap[bid] = new List<int>();

                            if (d.TryGetProperty("spellIds", out var sObj) && sObj.TryGetProperty("Array", out var sArr))
                            {
                                foreach (var sid in sArr.EnumerateArray()) breedMap[bid].Add(sid.GetInt32());
                            }
                        }

                        var insertCmd = connection.CreateCommand();
                        insertCmd.Transaction = transaction;
                        insertCmd.CommandText = "INSERT OR REPLACE INTO SpellVariants (BreedId, SpellIdsJson) VALUES ($bid, $json);";
                        insertCmd.Parameters.Add("$bid", SqliteType.Integer);
                        insertCmd.Parameters.Add("$json", SqliteType.Text);

                        foreach (var kvp in breedMap)
                        {
                            insertCmd.Parameters["$bid"].Value = kvp.Key;
                            insertCmd.Parameters["$json"].Value = System.Text.Json.JsonSerializer.Serialize(kvp.Value.Distinct().ToList());
                            insertCmd.ExecuteNonQuery();
                        }
                    }
                    transaction.Commit();
                }
                catch { transaction.Rollback(); }
            }
        }

        // =========================================================================
        // COMBAT DATA QUERIES
        // =========================================================================

        /// <summary>
        /// Retrieves the real stats for a monster at a specific grade index from the Monsters table.
        /// Returns null if the monster or grade is not found.
        /// </summary>
        public static MonsterGradeStats? GetMonsterGradeStats(int monsterId, int gradeIndex)
        {
            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Grades, Spells FROM Monsters WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", monsterId);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            string gradesJson = reader.GetString(0);
            string spellsRaw = reader.IsDBNull(1) ? "[]" : reader.GetString(1);

            var stats = new MonsterGradeStats();

            // Parse Grades JSON
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(gradesJson);
                var root = doc.RootElement;
                if (root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("Array", out var arrProp))
                    root = arrProp;

                if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var grades = root.EnumerateArray().ToList();
                    int idx = Math.Clamp(gradeIndex, 0, grades.Count - 1);
                    var g = grades[idx];

                    stats.Level = g.TryGetProperty("level", out var l) ? l.GetInt32() : 1;
                    stats.LifePoints = g.TryGetProperty("lifePoints", out var hp) ? hp.GetInt32() : 50;
                    stats.ActionPoints = g.TryGetProperty("actionPoints", out var ap) ? ap.GetInt32() : 6;
                    stats.MovementPoints = g.TryGetProperty("movementPoints", out var mp) ? mp.GetInt32() : 3;
                    stats.Strength = g.TryGetProperty("strength", out var str) ? str.GetInt32() : 0;
                    stats.Intelligence = g.TryGetProperty("intelligence", out var intl) ? intl.GetInt32() : 0;
                    stats.Chance = g.TryGetProperty("chance", out var cha) ? cha.GetInt32() : 0;
                    stats.Agility = g.TryGetProperty("agility", out var agi) ? agi.GetInt32() : 0;
                    stats.Wisdom = g.TryGetProperty("wisdom", out var wis) ? wis.GetInt32() : 0;
                    stats.NeutralResistance = g.TryGetProperty("neutralResistance", out var nr) ? nr.GetInt32() : 0;
                    stats.EarthResistance = g.TryGetProperty("earthResistance", out var er) ? er.GetInt32() : 0;
                    stats.FireResistance = g.TryGetProperty("fireResistance", out var fr) ? fr.GetInt32() : 0;
                    stats.WaterResistance = g.TryGetProperty("waterResistance", out var wr) ? wr.GetInt32() : 0;
                    stats.AirResistance = g.TryGetProperty("airResistance", out var ar) ? ar.GetInt32() : 0;
                    stats.GradeXp = g.TryGetProperty("gradeXp", out var xp) ? xp.GetInt32() : 100;
                    if (g.TryGetProperty("startingSpellId", out var ssp) && ssp.GetInt32() > 0)
                    {
                        stats.SpellIds.Add(ssp.GetInt32());
                    }
                }
            }
            catch { }

            // Parse Spells (can be in the Monsters row or from the grades)
            try
            {
                // Spells are stored as a JSON string, could be "[626, 4195]" or similar
                if (!string.IsNullOrEmpty(spellsRaw))
                {
                    using var spellDoc = System.Text.Json.JsonDocument.Parse(spellsRaw);
                    var spellRoot = spellDoc.RootElement;
                    if (spellRoot.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var s in spellRoot.EnumerateArray())
                        {
                            if (s.ValueKind == System.Text.Json.JsonValueKind.Number)
                                stats.SpellIds.Add(s.GetInt32());
                        }
                    }
                }
            }
            catch { }

            // La columna Monsters.Spells está vacía para los 5134 monstruos: el importador nunca la
            // rellenó. Los hechizos sí están en MonsterTemplates.Data, junto con el grado que le
            // corresponde a cada uno. Sin esto la IA se quedaba sin hechizos y recurría a uno
            // inventado de alcance 6, así que ningún monstruo necesitaba moverse jamás.
            //
            //   spells      : {"Array":[626, 4195]}
            //   spellGrades : {"Array":["3,11;3,12;...", "1,11;1,12;..."]}
            //
            // spellGrades[i] describe el hechizo spells[i]: una entrada "grado,nivel" por cada
            // grado del monstruo, separadas por ';'.
            if (stats.SpellIds.Count == 0)
            {
                try
                {
                    var tplCmd = connection.CreateCommand();
                    tplCmd.CommandText = "SELECT Data FROM MonsterTemplates WHERE Id = $id;";
                    tplCmd.Parameters.AddWithValue("$id", monsterId);
                    string? tplJson = tplCmd.ExecuteScalar() as string;

                    if (!string.IsNullOrEmpty(tplJson))
                    {
                        using var tplDoc = System.Text.Json.JsonDocument.Parse(tplJson);
                        var tplRoot = tplDoc.RootElement;

                        static List<System.Text.Json.JsonElement> UnwrapArray(System.Text.Json.JsonElement parent, string name)
                        {
                            if (!parent.TryGetProperty(name, out var prop)) return new List<System.Text.Json.JsonElement>();
                            if (prop.ValueKind == System.Text.Json.JsonValueKind.Object && prop.TryGetProperty("Array", out var inner))
                                prop = inner;
                            if (prop.ValueKind != System.Text.Json.JsonValueKind.Array) return new List<System.Text.Json.JsonElement>();
                            return prop.EnumerateArray().ToList();
                        }

                        var spellElems = UnwrapArray(tplRoot, "spells");
                        var gradeElems = UnwrapArray(tplRoot, "spellGrades");

                        for (int i = 0; i < spellElems.Count; i++)
                        {
                            if (spellElems[i].ValueKind != System.Text.Json.JsonValueKind.Number) continue;
                            int sid = spellElems[i].GetInt32();
                            if (sid <= 0) continue;

                            int spellGrade = 1;
                            if (i < gradeElems.Count && gradeElems[i].ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                string[] perGrade = (gradeElems[i].GetString() ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
                                if (perGrade.Length > 0)
                                {
                                    string entry = perGrade[Math.Clamp(gradeIndex, 0, perGrade.Length - 1)];
                                    string[] parts = entry.Split(',');
                                    if (parts.Length > 0 && int.TryParse(parts[0], out int g) && g > 0) spellGrade = g;
                                }
                            }

                            stats.SpellIds.Add(sid);
                            stats.SpellGrades[sid] = spellGrade;
                        }
                    }
                }
                catch { }
            }

            if (stats.SpellIds.Count == 0)
            {
                Program.LogDebug($"[DatabaseManager] WARN: el monstruo {monsterId} no tiene hechizos ni en Monsters ni en MonsterTemplates.");
            }

            return stats;
        }

        // Catálogo de efectos (tabla Effects, importada de data_assets_effectsdataroot). Dice qué
        // característica toca cada effectId; sin él no se puede aplicar nada que no sea daño.
        private static Dictionary<int, int>? _effectCharacteristics;

        private static int GetEffectCharacteristic(int effectId)
        {
            if (_effectCharacteristics == null)
            {
                var map = new Dictionary<int, int>();
                try
                {
                    using var conn = new SqliteConnection(WorldConnectionString);
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    // Sin filtrar por UseInFight: esa columna vale 0 justo para los efectos que
                    // nos interesan (el 1079 que quita PA y el 116 que quita alcance) y solo 31
                    // filas de toda la tabla la tienen a 1, así que no significa "se usa en
                    // combate". Con el filtro puesto, la Flecha Helada nunca retiraba los 2 PA.
                    cmd.CommandText = "SELECT Id, Characteristic FROM Effects WHERE Characteristic > 0;";
                    using var rd = cmd.ExecuteReader();
                    while (rd.Read()) map[rd.GetInt32(0)] = rd.IsDBNull(1) ? 0 : rd.GetInt32(1);
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[DatabaseManager] No se pudo cargar la tabla Effects: {ex.Message}");
                }
                _effectCharacteristics = map;
            }
            return _effectCharacteristics.TryGetValue(effectId, out int c) ? c : 0;
        }

        /// <summary>
        /// El arma equipada, expresada como si fuera un hechizo, para que el golpe con arma pase
        /// por el mismo camino que un lanzamiento normal.
        ///
        /// El coste en PA y el alcance salen de la ficha del objeto; el daño, de los efectos que
        /// tiene tirados ese ejemplar concreto. Los efectos 91-95 (robo) y 96-100 (daño) llevan su
        /// elemento en la propia tabla de efectos del cliente, así que no hace falta ninguna
        /// correspondencia escrita a mano.
        /// </summary>
        public static SpellCombatData? GetEquippedWeaponAsSpell(long characterId)
        {
            const int RanuraArma = 1;
            var arma = LoadInventory(characterId).FirstOrDefault(i => i.Position == RanuraArma);
            if (arma == null) return null;

            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Data FROM ItemTemplates WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", arma.ItemId);
            string? json = cmd.ExecuteScalar() as string;
            if (string.IsNullOrEmpty(json)) return null;

            var data = new SpellCombatData
            {
                SpellId = 0,
                SpellLevelId = 0,
                APCost = 3,
                MinRange = 1,
                MaxRange = 1,
                BaseDamageMin = 0,
                BaseDamageMax = 0,
                NeedsLineOfSight = true
            };

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("apCost", out var ap)) data.APCost = ap.GetInt32();
                if (root.TryGetProperty("minRange", out var mr)) data.MinRange = mr.GetInt32();
                if (root.TryGetProperty("range", out var r)) data.MaxRange = r.GetInt32();
                if (root.TryGetProperty("criticalHitProbability", out var cp)) data.CriticalHitProbability = cp.GetInt32();
            }
            catch { }

            // Daño del ejemplar equipado. Cada efecto trae su elemento en la tabla Effects.
            foreach (var kv in arma.Effects)
            {
                if (kv.Key < 91 || kv.Key > 100 || kv.Value <= 0) continue;

                var elemCmd = connection.CreateCommand();
                elemCmd.CommandText = "SELECT ElementId FROM Effects WHERE Id = $id;";
                elemCmd.Parameters.AddWithValue("$id", kv.Key);
                object? elem = elemCmd.ExecuteScalar();
                if (elem == null || elem == DBNull.Value) continue;

                data.Element = Convert.ToInt32(elem);
                data.BaseDamageMin = kv.Value;
                data.BaseDamageMax = kv.Value;
                break;
            }

            return data.BaseDamageMin > 0 ? data : null;
        }

        /// <summary>
        /// Hechizos que el personaje tiene disponibles a su nivel.
        ///
        /// SpellVariants.SpellIdsJson viene INTERCALADO: hechizo base, su variante, hechizo base,
        /// su variante... (comprobado con las traducciones: 'Flecha Helada' (base), 'Flecha
        /// Acosante' (variante), 'Flecha de Pelea' (base)...). Nos quedamos con los de índice par
        /// y filtramos por el nivel mínimo de cada hechizo.
        ///
        /// La usan tanto el jvn de combate como la barra de accesos directos de roleplay, para
        /// que el jugador vea la misma lista en los dos sitios.
        /// </summary>
        public static List<int> GetPlayerAvailableSpells(int breedId, int level)
        {
            return GetBreedSpellIds(breedId)
                .Where((_, index) => index % 2 == 0)
                .Where(id => GetSpellMinPlayerLevel(id) <= level)
                .ToList();
        }

        /// <summary>
        /// Tabla de botín de un monstruo, ya resuelta para el grado indicado.
        ///
        /// Sale de MonsterTemplates.Data → drops[], donde cada entrada trae el objeto y su
        /// probabilidad por grado (percentDropForGrade1..5). Ejemplo del Capiorico Rojo:
        /// pluma de pío rojo al 100 %, semillas de sésamo al 18 %, bolsita de limones al 3 %.
        ///
        /// Se descartan las entradas con criterios (`hasCriterions`), porque son condicionales
        /// —de misión o de logro— y su lenguaje de criterios ("Qo=13820&amp;PO!19649&amp;…") no está
        /// implementado. Es preferible no soltarlas a soltarlas siempre.
        /// </summary>
        public static List<MonsterDrop> GetMonsterDrops(int monsterId, int gradeIndex)
        {
            var drops = new List<MonsterDrop>();
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();

                var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT Data FROM MonsterTemplates WHERE Id = $id;";
                cmd.Parameters.AddWithValue("$id", monsterId);
                string? json = cmd.ExecuteScalar() as string;
                if (string.IsNullOrEmpty(json)) return drops;

                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("drops", out var dropsProp)) return drops;
                if (dropsProp.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    dropsProp.TryGetProperty("Array", out var inner)) dropsProp = inner;
                if (dropsProp.ValueKind != System.Text.Json.JsonValueKind.Array) return drops;

                // percentDropForGrade1..5; los grados por encima del quinto reutilizan el último.
                string percentKey = "percentDropForGrade" + Math.Clamp(gradeIndex + 1, 1, 5);

                foreach (var e in dropsProp.EnumerateArray())
                {
                    if (e.TryGetProperty("hasCriterions", out var hc) && hc.GetInt32() != 0) continue;
                    if (!e.TryGetProperty("objectId", out var oid)) continue;

                    double pct = 0;
                    if (e.TryGetProperty(percentKey, out var p)) pct = p.GetDouble();
                    else if (e.TryGetProperty("percentDropForGrade1", out var p1)) pct = p1.GetDouble();
                    if (pct <= 0) continue;

                    drops.Add(new MonsterDrop { ObjectId = oid.GetInt32(), PercentDrop = pct });
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[DatabaseManager] Error leyendo el botín del monstruo {monsterId}: {ex.Message}");
            }
            return drops;
        }

        /// <summary>
        /// Mete un objeto en el inventario. Si ya hay uno del mismo tipo suelto en la bolsa, suma
        /// a esa pila en vez de crear otra entrada. Devuelve el objeto resultante.
        /// </summary>
        public static PlayerItem AddItemToInventory(long characterId, int itemGid, int quantity)
        {
            var inventory = LoadInventory(characterId);
            var existing = inventory.FirstOrDefault(i => i.ItemId == itemGid && i.Position == 63);

            if (existing != null)
            {
                existing.Quantity += quantity;
                SaveInventoryItem(characterId, existing);
                return existing;
            }

            long maxUid = inventory.Count > 0 ? inventory.Max(i => i.Uid) : 0;
            var item = new PlayerItem
            {
                Uid = Math.Max(maxUid + 1, 1),
                ItemId = itemGid,
                Quantity = quantity,
                Position = 63
            };
            SaveInventoryItem(characterId, item);
            return item;
        }

        /// <summary>
        /// Retrieves spell level data (AP cost, range, base damage, element) for a given spell ID.
        /// Queries the SpellLevels table and parses the effects JSON.
        /// </summary>
        public static SpellCombatData? GetSpellCombatData(int spellId, int grade = 1)
        {
            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Id, APCost, MinRange, MaxRange, EffectsJson, CastTestLos, CastInLine, MaxCastPerTurn, MaxCastPerTarget, CriticalHitProbability, CriticalEffectsJson FROM SpellLevels WHERE SpellId = $sid AND Grade = $g LIMIT 1;";
            cmd.Parameters.AddWithValue("$sid", spellId);
            cmd.Parameters.AddWithValue("$g", grade);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                // Fallback: try grade 1
                cmd.Parameters["$g"].Value = 1;
                reader.Close();
                using var reader2 = cmd.ExecuteReader();
                if (!reader2.Read()) return null;
                return ParseSpellCombatData(reader2);
            }
            return ParseSpellCombatData(reader);
        }

        private static SpellCombatData ParseSpellCombatData(SqliteDataReader reader)
        {
            var data = new SpellCombatData
            {
                SpellLevelId = reader.GetInt32(0),
                APCost = reader.GetInt32(1),
                MinRange = reader.GetInt32(2),
                MaxRange = reader.GetInt32(3),
                NeedsLineOfSight = reader.IsDBNull(5) || reader.GetInt32(5) == 1,
                CastInLine = !reader.IsDBNull(6) && reader.GetInt32(6) == 1,
                MaxCastPerTurn = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                MaxCastPerTarget = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                CriticalHitProbability = reader.IsDBNull(9) ? 0 : reader.GetInt32(9)
            };

            // Daño del golpe crítico. Viene en su propia lista de efectos: la Flecha Helada hace
            // 12-14 de agua normal y 15-17 en crítico, con el mismo -2 PA.
            try
            {
                string critJson = reader.IsDBNull(10) ? "" : reader.GetString(10);
                if (!string.IsNullOrEmpty(critJson))
                {
                    using var critDoc = System.Text.Json.JsonDocument.Parse(critJson);
                    if (critDoc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var e in critDoc.RootElement.EnumerateArray())
                        {
                            int eid = e.TryGetProperty("effectId", out var ce) ? ce.GetInt32() : 0;
                            if (eid < 96 || eid > 100) continue;
                            data.CriticalDamageMin = e.TryGetProperty("diceNum", out var cdn) ? cdn.GetInt32() : 0;
                            data.CriticalDamageMax = e.TryGetProperty("diceSide", out var cds) ? cds.GetInt32() : 0;
                            break;
                        }
                    }
                }
            }
            catch { }

            string effectsJson = reader.IsDBNull(4) ? "[]" : reader.GetString(4);
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(effectsJson);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var e in doc.RootElement.EnumerateArray())
                    {
                        int effectId = e.TryGetProperty("effectId", out var eid) ? eid.GetInt32() : 0;
                        // effectIds 96-100 are damage effects (96=Water, 97=Earth, 98=Air, 99=Fire, 100=Neutral)
                        if (effectId >= 96 && effectId <= 100)
                        {
                            data.BaseDamageMin = e.TryGetProperty("diceNum", out var dn) ? dn.GetInt32() : 5;
                            data.BaseDamageMax = e.TryGetProperty("diceSide", out var ds) ? ds.GetInt32() : 10;

                            // El elemento viene dado en el propio efecto (effectElement). Es el
                            // mismo número que el cliente espera en el paquete de daño: el hechizo
                            // 13425 trae effectElement=2 y la captura oficial manda f25.f1=2.
                            // El switch por effectId solo se usa si falta el campo.
                            data.Element = e.TryGetProperty("effectElement", out var ee)
                                ? ee.GetInt32()
                                : effectId switch
                                {
                                    96 => 3,  // Agua
                                    97 => 1,  // Tierra
                                    98 => 4,  // Aire
                                    99 => 2,  // Fuego
                                    100 => 0, // Neutral
                                    _ => 0
                                };
                            continue; // seguimos recorriendo: un hechizo puede dañar y además empujar
                        }

                        int dice = e.TryGetProperty("diceNum", out var dnum) ? dnum.GetInt32() : 0;
                        int dur = e.TryGetProperty("duration", out var dur0) ? dur0.GetInt32() : 0;

                        // 5 = empujar, 6 = atraer; las casillas van en diceNum.
                        // La Flecha de Retroceso (32426) es 98 (aire 15-17) + 5 (empuje de 2).
                        if (effectId == 5 || effectId == 6)
                        {
                            if (dice > 0) data.PushDistance = effectId == 5 ? dice : -dice;
                            continue;
                        }

                        // Efecto 293: sube el daño base de un hechizo concreto durante unos turnos
                        // ("Flecha Helada: +4 de daños básicos - 3 turnos"). El hechizo afectado
                        // viaja en diceNum, la bonificación en value y los turnos en duration.
                        if (effectId == 293)
                        {
                            int hechizoAfectado = dice;
                            int bonif = e.TryGetProperty("value", out var val293) ? val293.GetInt32() : 0;
                            if (hechizoAfectado > 0 && bonif != 0)
                            {
                                data.DamageBuffs.Add(new SpellDamageBuff
                                {
                                    SpellId = hechizoAfectado,
                                    Bonus = bonif,
                                    Duration = dur > 0 ? dur : 1
                                });
                            }
                            continue;
                        }

                        // Cualquier otro efecto que toque una característica del objetivo. La
                        // característica sale del catálogo de efectos del cliente, no de una
                        // lista escrita a mano: el 1079 de la Flecha Helada quita PA
                        // (característica 1) y el 116 del pío quita alcance (característica 19).
                        int carac = GetEffectCharacteristic(effectId);
                        if (carac > 0 && dice != 0)
                        {
                            data.StatEffects.Add(new SpellStatEffect
                            {
                                EffectId = effectId,
                                Characteristic = carac,
                                Value = -dice,
                                Duration = dur
                            });
                        }
                    }
                }
            }
            catch { }

            return data;
        }

        public static List<int> GetBreedSpellIds(int breedId)
        {
            var spells = new List<int>();
            try
            {
                using var conn = new SqliteConnection(WorldConnectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT SpellIdsJson FROM SpellVariants WHERE BreedId = @breedId;";
                cmd.Parameters.AddWithValue("@breedId", breedId);
                var result = cmd.ExecuteScalar()?.ToString();
                if (!string.IsNullOrEmpty(result))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(result);
                    foreach (var elem in doc.RootElement.EnumerateArray())
                    {
                        spells.Add(elem.GetInt32());
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager] Error fetching breed spells for breed {breedId}: {ex.Message}");
            }

            if (spells.Count == 0)
            {
                // Sin respaldo inventado: si la raza no tiene hechizos en la base de datos es un
                // problema de datos y hay que verlo, no taparlo con ids de otra clase.
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[DatabaseManager][WARN] La raza {breedId} no tiene hechizos en SpellVariants. " +
                                  "El personaje se quedara sin hechizos en combate.");
                Console.ResetColor();
            }
            return spells;
        }

        /// <summary>
        /// Minimum character level at which a spell unlocks, per SpellLevels.
        /// Devuelve 1 si no consta, para no ocultar hechizos por falta de datos.
        /// </summary>
        public static int GetSpellMinPlayerLevel(int spellId)
        {
            try
            {
                using var conn = new SqliteConnection(WorldConnectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT MIN(MinPlayerLevel) FROM SpellLevels WHERE SpellId = @sid;";
                cmd.Parameters.AddWithValue("@sid", spellId);
                var result = cmd.ExecuteScalar();
                if (result != null && result != DBNull.Value && int.TryParse(result.ToString(), out int lvl) && lvl > 0)
                {
                    return lvl;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager] Error leyendo MinPlayerLevel del hechizo {spellId}: {ex.Message}");
            }
            return 1;
        }
    }

    // =========================================================================
    // COMBAT DATA TRANSFER OBJECTS
    // =========================================================================

    public class MonsterGradeStats
    {
        public int Level { get; set; } = 1;
        public int LifePoints { get; set; } = 50;
        public int ActionPoints { get; set; } = 6;
        public int MovementPoints { get; set; } = 3;
        public int Strength { get; set; }
        public int Intelligence { get; set; }
        public int Chance { get; set; }
        public int Agility { get; set; }
        public int Wisdom { get; set; }
        public int NeutralResistance { get; set; }
        public int EarthResistance { get; set; }
        public int FireResistance { get; set; }
        public int WaterResistance { get; set; }
        public int AirResistance { get; set; }
        public int GradeXp { get; set; } = 100;
        public List<int> SpellIds { get; set; } = new List<int>();

        /// <summary>Grado (nivel) de cada hechizo del monstruo, indexado por id de hechizo.</summary>
        public Dictionary<int, int> SpellGrades { get; set; } = new Dictionary<int, int>();
    }

    public class MonsterDrop
    {
        public int ObjectId { get; set; }
        /// <summary>Probabilidad en tanto por ciento para el grado del monstruo.</summary>
        public double PercentDrop { get; set; }
    }

    public class SpellCombatData
    {
        public long SpellId { get; set; }
        public int SpellLevelId { get; set; }
        public int APCost { get; set; } = 3;
        public int MinRange { get; set; } = 1;
        public int MaxRange { get; set; } = 1;
        public int BaseDamageMin { get; set; } = 5;
        public int BaseDamageMax { get; set; } = 10;
        public int BaseDamage => (BaseDamageMin + BaseDamageMax) / 2;
        public int EffectUid { get; set; } = 41870;
        public int Element { get; set; } = 0; // 0=Neutral, 1=Tierra, 2=Fuego, 3=Agua, 4=Aire

        /// <summary>
        /// Si el hechizo exige línea de visión (castTestLos en los datos del cliente).
        /// </summary>
        public bool NeedsLineOfSight { get; set; } = true;

        /// <summary>Solo se puede lanzar en línea recta.</summary>
        public bool CastInLine { get; set; }

        public int MaxCastPerTurn { get; set; }

        /// <summary>Lanzamientos permitidos por turno sobre un MISMO objetivo. 0 = sin límite.</summary>
        public int MaxCastPerTarget { get; set; }

        /// <summary>
        /// Probabilidad base de golpe crítico, en tanto por ciento. Se le suma el crítico que dé
        /// el equipo: la Flecha Helada trae 10 y el Dofus Turquesa otros 10, de ahí el 20 % que
        /// muestra la descripción del hechizo.
        /// </summary>
        public int CriticalHitProbability { get; set; }

        public int CriticalDamageMin { get; set; }
        public int CriticalDamageMax { get; set; }
        public bool HasCriticalDamage => CriticalDamageMin > 0 || CriticalDamageMax > 0;

        /// <summary>Casillas de desplazamiento: positivo empuja, negativo atrae. 0 = no mueve.</summary>
        public int PushDistance { get; set; }

        /// <summary>
        /// Efectos que modifican una característica del objetivo (quitar PA, quitar alcance,
        /// bonificaciones...). Cada uno trae ya la característica que le corresponde según el
        /// catálogo de efectos del cliente.
        /// </summary>
        public List<SpellStatEffect> StatEffects { get; set; } = new List<SpellStatEffect>();

        /// <summary>Bonificaciones de daño base que este hechizo deja puestas al lanzarlo.</summary>
        public List<SpellDamageBuff> DamageBuffs { get; set; } = new List<SpellDamageBuff>();
    }

    public class SpellDamageBuff
    {
        /// <summary>Hechizo cuyo daño base sube.</summary>
        public int SpellId { get; set; }
        public int Bonus { get; set; }
        /// <summary>Turnos que dura desde que se pone.</summary>
        public int Duration { get; set; } = 1;
    }

    public class SpellStatEffect
    {
        public int EffectId { get; set; }
        public int Characteristic { get; set; }
        public int Value { get; set; }
        public int Duration { get; set; }
    }
}
