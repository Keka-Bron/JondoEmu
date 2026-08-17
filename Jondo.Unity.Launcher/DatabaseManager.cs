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

                // Seed the built-in test accounts if the table is empty (credentials below)
                var seedAccount = authConnection.CreateCommand();
                seedAccount.CommandText = @"
                    INSERT OR IGNORE INTO Accounts (Id, Login, Password, Nickname) VALUES (188940901, 'keka', 'test', 'Keka');
                    INSERT OR IGNORE INTO Accounts (Id, Login, Password, Nickname) VALUES (188940902, 'dragonlord', 'test', 'DragonLord');
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

                // Migration: the character's accumulated experience column.
                try
                {
                    var addXpCmd = worldConnection.CreateCommand();
                    addXpCmd.CommandText = "ALTER TABLE Characters ADD COLUMN Experience INTEGER NOT NULL DEFAULT 0;";
                    addXpCmd.ExecuteNonQuery();
                    Console.WriteLine("[SQLite] Migration: Added Experience column to Characters table.");
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // Already exists.
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

                // Servers table. Until now the list came from a fixed binary template, so there
                // was no way to tell which character lived on which server.
                //
                //   Type     what the protocol sends in the server list. It groups servers by
                //            category; the values come from the real capture.
                //   Status   what is advertised over HTTP, and what decides the server's colour
                //            on the selection screen.
                //   Joinable whether the server accepts connections. Checked here too, not only
                //            on the client.
                var createServers = worldConnection.CreateCommand();
                createServers.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Servers (
                        Id INTEGER PRIMARY KEY,
                        Name TEXT NOT NULL,
                        Type INTEGER NOT NULL DEFAULT 1,
                        Status INTEGER NOT NULL DEFAULT 3,
                        Joinable INTEGER NOT NULL DEFAULT 0,
                        IsDefault INTEGER NOT NULL DEFAULT 0
                    );
                ";
                createServers.ExecuteNonQuery();

                foreach (string column in new[]
                         {
                             "Type INTEGER NOT NULL DEFAULT 1",
                             "Status INTEGER NOT NULL DEFAULT 3",
                             "Joinable INTEGER NOT NULL DEFAULT 0"
                         })
                {
                    try
                    {
                        var addCmd = worldConnection.CreateCommand();
                        addCmd.CommandText = $"ALTER TABLE Servers ADD COLUMN {column};";
                        addCmd.ExecuteNonQuery();
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException)
                    {
                        // Already exists.
                    }
                }

                SeedServers(worldConnection);

                // Migration: which server each character belongs to.
                try
                {
                    var addServerCmd = worldConnection.CreateCommand();
                    addServerCmd.CommandText =
                        $"ALTER TABLE Characters ADD COLUMN ServerId INTEGER NOT NULL DEFAULT {DefaultServerId};";
                    addServerCmd.ExecuteNonQuery();
                    Console.WriteLine("[SQLite] Migration: Added ServerId column to Characters table.");
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // Already exists.
                }

                // Migration: last connection, shown per server on the character list.
                try
                {
                    var addLastConnCmd = worldConnection.CreateCommand();
                    addLastConnCmd.CommandText = "ALTER TABLE Characters ADD COLUMN LastConnection TEXT;";
                    addLastConnCmd.ExecuteNonQuery();
                    Console.WriteLine("[SQLite] Migration: Added LastConnection column to Characters table.");
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // Already exists.
                }

                // Migration: head chosen at creation. Its skin travels in the look, and a
                // character without it is drawn with no face.
                try
                {
                    var addHeadCmd = worldConnection.CreateCommand();
                    addHeadCmd.CommandText = "ALTER TABLE Characters ADD COLUMN HeadId INTEGER;";
                    addHeadCmd.ExecuteNonQuery();
                    Console.WriteLine("[SQLite] Migration: Added HeadId column to Characters table.");
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // Already exists.
                }

                // Migración: lo que mide el personaje, en tanto por ciento de lo que mide su raza.
                // Cien es el tamaño de siempre, así que los que ya existían no cambian de aspecto
                // al aparecer la columna.
                try
                {
                    var addSizeCmd = worldConnection.CreateCommand();
                    addSizeCmd.CommandText = "ALTER TABLE Characters ADD COLUMN Size INTEGER NOT NULL DEFAULT 100;";
                    addSizeCmd.ExecuteNonQuery();
                    Console.WriteLine("[SQLite] Migration: Added Size column to Characters table.");
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // Already exists.
                }

                FillMissingHeads(worldConnection);

                // A character with no date leaves the server-selection screen empty, so no row is
                // allowed to stay without one. This covers the characters that already existed
                // when the column was added.
                try
                {
                    var fillLastConn = worldConnection.CreateCommand();
                    fillLastConn.CommandText =
                        "UPDATE Characters SET LastConnection = $now " +
                        "WHERE LastConnection IS NULL OR LastConnection = '';";
                    fillLastConn.Parameters.AddWithValue("$now",
                        DateTimeOffset.Now.ToString(Network.ConnectionProtocol.ConnectionDateFormat));
                    int filled = fillLastConn.ExecuteNonQuery();
                    if (filled > 0)
                    {
                        Console.WriteLine($"[SQLite] Migration: filled in the last connection of {filled} character(s).");
                    }
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex)
                {
                    Console.WriteLine($"[SQLite] Could not fill in the last connection: {ex.Message}");
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

                // Cuál de cada pareja de hechizo lleva el personaje. Es lo único de los hechizos
                // que no sale de los datos del cliente: las parejas y los niveles son suyos, pero
                // la elección es del jugador y tiene que sobrevivir de una sesión a la siguiente.
                var createSpellChoices = worldConnection.CreateCommand();
                createSpellChoices.CommandText = @"
                    CREATE TABLE IF NOT EXISTS CharacterSpellChoices (
                        CharacterId INTEGER NOT NULL,
                        PairId INTEGER NOT NULL,
                        SpellId INTEGER NOT NULL,
                        PRIMARY KEY (CharacterId, PairId)
                    );
                ";
                createSpellChoices.ExecuteNonQuery();

                // Y en qué hueco de la barra puso cada hechizo, por lo mismo.
                var createSpellBar = worldConnection.CreateCommand();
                createSpellBar.CommandText = @"
                    CREATE TABLE IF NOT EXISTS CharacterSpellBar (
                        CharacterId INTEGER NOT NULL,
                        Slot INTEGER NOT NULL,
                        SpellId INTEGER NOT NULL,
                        PRIMARY KEY (CharacterId, Slot)
                    );
                ";
                createSpellBar.ExecuteNonQuery();

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
                    CREATE TABLE IF NOT EXISTS Dungeons (
                        Id INTEGER PRIMARY KEY,
                        Name TEXT,
                        MinLevel INTEGER NOT NULL DEFAULT 1,
                        OptimalLevel INTEGER NOT NULL DEFAULT 1,
                        Difficulty INTEGER NOT NULL DEFAULT 0,
                        EntranceMapId INTEGER NOT NULL DEFAULT 0,
                        ExitMapId INTEGER NOT NULL DEFAULT 0,
                        Bosses TEXT
                    );
                    CREATE TABLE IF NOT EXISTS DungeonRooms (
                        DungeonId INTEGER NOT NULL,
                        Position INTEGER NOT NULL,
                        MapId INTEGER NOT NULL,
                        PRIMARY KEY (DungeonId, Position)
                    );
                    CREATE INDEX IF NOT EXISTS idx_dungeon_rooms_map ON DungeonRooms(MapId);

                    /* Los índices de los hechizos, que NO son un adorno: son la diferencia entre
                       un combate fluido y uno a trompicones.

                       SpellLevels tiene 34.823 filas y sus dos columnas de efectos suman 67 MB de
                       texto, casi dos kilobytes por fila. La clave primaria es Id, pero TODAS las
                       consultas del combate buscan por SpellId —los efectos de un hechizo, su
                       grado, su coste, sus recargas—, así que cada una recorría la tabla entera:
                       medido, 37 milisegundos por consulta y cuatro consultas por lanzamiento.
                       Eso es el parón de entre 47 y 138 milisegundos que se notaba al lanzar.

                       Con el índice, la misma consulta pasa de SCAN a SEARCH y baja a cuatro
                       milésimas de milisegundo. Crearlos cuesta 92 milisegundos una sola vez. */
                    CREATE INDEX IF NOT EXISTS idx_spelllevels_hechizo ON SpellLevels(SpellId, Grade);
                    CREATE INDEX IF NOT EXISTS idx_spelllevels_nivel ON SpellLevels(SpellId, MinPlayerLevel);
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
                    Console.WriteLine("[SQLite] Seeded the default character.");
                }
                else
                {
                    // Migration: strip the leftover bracket/hash markers from the seeded name
                    using (var updateCmd = worldConnection.CreateCommand())
                    {
                        updateCmd.CommandText = "UPDATE Characters SET Name = 'CADERNIS' WHERE Name = '[#CADERNIS#]' OR Name = '#CADERNIS#';";
                        int affected = updateCmd.ExecuteNonQuery();
                        if (affected > 0)
                        {
                            Console.WriteLine("[SQLite] Migration: Normalized the seeded character name.");
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

                    // Migration: Ensure character Breed is 9 (Cra)
                    using (var updateBreedCmd = worldConnection.CreateCommand())
                    {
                        updateBreedCmd.CommandText = "UPDATE Characters SET Breed = 9 WHERE Id = 13825558 AND Breed <> 9;";
                        int breedAffected = updateBreedCmd.ExecuteNonQuery();
                        if (breedAffected > 0)
                        {
                            Console.WriteLine("[SQLite] Migration: Updated character Breed to 9 (Cra).");
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

        // --- Auth Operations & Security (Anti-SQL Injection & Anti-DDoS) ---

        public class DbAccount
        {
            public long Id { get; set; }
            public string Login { get; set; } = "";
            public string Nickname { get; set; } = "";
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int attempts, DateTime lockTime)> _ipLockouts 
            = new System.Collections.Concurrent.ConcurrentDictionary<string, (int attempts, DateTime lockTime)>();

        public static bool ValidateAccountCredentials(string login, string password, string clientIp, out DbAccount? account, out string errorMessage)
        {
            account = null;
            errorMessage = "";

            // 1. Anti-DDoS Lockout check
            if (_ipLockouts.TryGetValue(clientIp, out var lockData))
            {
                if (lockData.attempts >= 5)
                {
                    double remainingSeconds = 60 - (DateTime.UtcNow - lockData.lockTime).TotalSeconds;
                    if (remainingSeconds > 0)
                    {
                        errorMessage = $"[Anti-DDoS] Too many failed attempts. Temporarily locked out for {Math.Ceiling(remainingSeconds)} s.";
                        return false;
                    }
                    else
                    {
                        _ipLockouts.TryRemove(clientIp, out _);
                    }
                }
            }

            // 2. Anti-SQL Injection & Input Sanitization
            login = (login ?? "").Trim().ToLowerInvariant();
            password = (password ?? "").Trim();

            if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(password))
            {
                errorMessage = "Please enter a username and a password.";
                return false;
            }

            if (!System.Text.RegularExpressions.Regex.IsMatch(login, @"^[a-zA-Z0-9_@.-]{3,32}$"))
            {
                errorMessage = "The username contains invalid characters or has the wrong length (3-32 characters).";
                RecordFailedAttempt(clientIp);
                return false;
            }

            // 3. Parametrized Query against auth.db
            try
            {
                using var connection = new SqliteConnection(AuthConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT Id, Login, Nickname FROM Accounts WHERE LOWER(Login) = $login AND Password = $pass;";
                command.Parameters.AddWithValue("$login", login);
                command.Parameters.AddWithValue("$pass", password);

                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    account = new DbAccount
                    {
                        Id = reader.GetInt64(0),
                        Login = reader.GetString(1),
                        Nickname = reader.GetString(2)
                    };
                    _ipLockouts.TryRemove(clientIp, out _);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager Error] Authentication error: {ex.Message}");
            }

            RecordFailedAttempt(clientIp);
            errorMessage = "Wrong username or password.";
            return false;
        }

        public static bool RegisterNewAccount(string login, string password, string nickname, string clientIp, out string errorMessage)
        {
            errorMessage = "";

            login = (login ?? "").Trim().ToLowerInvariant();
            password = (password ?? "").Trim();
            nickname = (nickname ?? "").Trim();

            if (!System.Text.RegularExpressions.Regex.IsMatch(login, @"^[a-zA-Z0-9_@.-]{3,32}$"))
            {
                errorMessage = "The username may only contain letters, digits and the characters _ @ . - (3-32 characters).";
                return false;
            }

            if (password.Length < 3 || password.Length > 32)
            {
                errorMessage = "The password must be between 3 and 32 characters long.";
                return false;
            }

            if (string.IsNullOrEmpty(nickname)) nickname = login;

            try
            {
                using var connection = new SqliteConnection(AuthConnectionString);
                connection.Open();

                var checkCmd = connection.CreateCommand();
                checkCmd.CommandText = "SELECT COUNT(*) FROM Accounts WHERE LOWER(Login) = $login;";
                checkCmd.Parameters.AddWithValue("$login", login);
                if ((long)(checkCmd.ExecuteScalar() ?? 0L) > 0)
                {
                    errorMessage = "That username is already registered.";
                    return false;
                }

                var insertCmd = connection.CreateCommand();
                insertCmd.CommandText = "INSERT INTO Accounts (Login, Password, Nickname) VALUES ($login, $pass, $nick);";
                insertCmd.Parameters.AddWithValue("$login", login);
                insertCmd.Parameters.AddWithValue("$pass", password);
                insertCmd.Parameters.AddWithValue("$nick", nickname);
                insertCmd.ExecuteNonQuery();

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"Error registering the account: {ex.Message}";
                return false;
            }
        }

        private static void RecordFailedAttempt(string clientIp)
        {
            _ipLockouts.AddOrUpdate(clientIp, 
                (1, DateTime.UtcNow), 
                (key, old) => (old.attempts + 1, DateTime.UtcNow));
        }

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

        public static DbAccount? GetAccountById(long accountId)
        {
            if (accountId <= 0) return null;
            try
            {
                using var connection = new SqliteConnection(AuthConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT Id, Login, Nickname FROM Accounts WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", accountId);

                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    return new DbAccount
                    {
                        Id = reader.GetInt64(0),
                        Login = reader.GetString(1),
                        Nickname = reader.GetString(2)
                    };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager] Error reading account {accountId}: {ex.Message}");
            }
            return null;
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

        // --- Servers ---

        /// <summary>
        /// The server the emulated world lives on. The client resolves each server's name and
        /// artwork by id against its own data, so we use one it already knows about.
        /// </summary>
        public const int DefaultServerId = 290;

        /// <summary>Where a character starts, and where one goes back to if it ends up nowhere.
        /// The same values the Characters table gives a new row.</summary>
        public const long StartingMap = 154010884L;
        public const int StartingCell = 315;

        /// <summary>Status advertised over HTTP for a server that can be joined.</summary>
        public const int ServerStatusOnline = 3;

        /// <summary>
        /// Status of a server that shows up in the list but does not accept connections. This is
        /// the most likely value according to the classic Dofus enum, where 3 means online; it
        /// could not be verified against a capture because that traffic is encrypted. If the
        /// client does not render it as expected, change it with an UPDATE on the Servers table.
        /// </summary>
        public const int ServerStatusNoJoin = 4;

        public class DbServer
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            /// <summary>Server category, exactly as it travels in the protocol's server list.</summary>
            public int Type { get; set; } = 1;
            /// <summary>Status advertised over HTTP; decides the colour on the selection screen.</summary>
            public int Status { get; set; } = ServerStatusOnline;
            /// <summary>Whether it accepts connections. Checked here, not only on the client.</summary>
            public bool Joinable { get; set; }
            public bool IsDefault { get; set; }
        }

        /// <summary>
        /// The servers on offer. Only one of them is open: the rest show up in the list but
        /// cannot be joined, so the screen looks populated without promising worlds that do not
        /// exist. The ids and their category come from the real capture; the client resolves
        /// each server's name against its own data.
        /// </summary>
        private static void SeedServers(SqliteConnection connection)
        {
            // (id, category)
            var closed = new (int Id, int Type)[]
            {
                (291, 1), (292, 1), (293, 1), (294, 1), (295, 0),
                (350, 3), (351, 3), (352, 3),
                (353, 2), (354, 2), (355, 2),
                (99, 4), (50, 5)
            };

            var open = connection.CreateCommand();
            open.CommandText =
                "INSERT OR IGNORE INTO Servers (Id, Name, Type, Status, Joinable, IsDefault) " +
                "VALUES ($id, $name, 1, $status, 1, 1);";
            open.Parameters.AddWithValue("$id", DefaultServerId);
            open.Parameters.AddWithValue("$name", DefaultServerName);
            open.Parameters.AddWithValue("$status", ServerStatusOnline);
            open.ExecuteNonQuery();

            foreach (var (id, type) in closed)
            {
                var cmd = connection.CreateCommand();
                cmd.CommandText =
                    "INSERT OR IGNORE INTO Servers (Id, Name, Type, Status, Joinable, IsDefault) " +
                    "VALUES ($id, $name, $type, $status, 0, 0);";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$name", "Server " + id);
                cmd.Parameters.AddWithValue("$type", type);
                cmd.Parameters.AddWithValue("$status", ServerStatusNoJoin);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Name of the open server. Only used by the emulator's own logs.</summary>
        public const string DefaultServerName = "Tal Kasha";

        public static List<DbServer> GetServers()
        {
            var list = new List<DbServer>();
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT Id, Name, Type, Status, Joinable, IsDefault FROM Servers ORDER BY IsDefault DESC, Id;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new DbServer
                    {
                        Id = reader.GetInt32(0),
                        Name = reader.GetString(1),
                        Type = reader.GetInt32(2),
                        Status = reader.GetInt32(3),
                        Joinable = reader.GetInt32(4) != 0,
                        IsDefault = reader.GetInt32(5) != 0
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager] Error reading the server list: {ex.Message}");
            }

            // With no table (an old database) we at least offer the open server, which is where
            // the migration left every character.
            if (list.Count == 0)
            {
                list.Add(new DbServer
                {
                    Id = DefaultServerId,
                    Name = DefaultServerName,
                    Type = 1,
                    Status = ServerStatusOnline,
                    Joinable = true,
                    IsDefault = true
                });
            }
            return list;
        }

        /// <summary>Checks that a server exists and accepts connections.</summary>
        public static bool IsServerJoinable(int serverId)
        {
            foreach (var server in GetServers())
            {
                if (server.Id == serverId) return server.Joinable;
            }
            return false;
        }

        // --- Character Operations ---

        public class DbCharacter
        {
            public long Id { get; set; }
            public long AccountId { get; set; }
            public int ServerId { get; set; } = DefaultServerId;
            public string Name { get; set; }
            public int Breed { get; set; }
            public int Sex { get; set; }
            public int Level { get; set; }
            public string LookHex { get; set; }
            /// <summary>ISO date of the last connection, empty if the character never logged in.</summary>
            public string LastConnection { get; set; } = "";
            /// <summary>Head chosen at creation. Its skin is part of the look.</summary>
            public int HeadId { get; set; }
        }

        /// <summary>
        /// The characters on an account. If a server is given, only the ones on that server.
        ///
        /// There is no fallback of any kind: if the account has no characters, the list comes
        /// back empty. The fallback that used to be here returned another account's characters
        /// whenever the query found nothing, which with several accounts meant showing one
        /// player the characters of another.
        /// </summary>
        public static List<DbCharacter> GetCharactersByAccountId(long accountId, int serverId = 0)
        {
            var list = new List<DbCharacter>();
            if (accountId <= 0) return list;

            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText =
                "SELECT Id, Name, Breed, Sex, Level, Look, " +
                "COALESCE(ServerId, $defaultServer), COALESCE(LastConnection, ''), " +
                "COALESCE(HeadId, 0) " +
                "FROM Characters WHERE AccountId = $accId" +
                (serverId > 0 ? " AND COALESCE(ServerId, $defaultServer) = $serverId" : "") +
                " ORDER BY Id;";
            command.Parameters.AddWithValue("$accId", accountId);
            command.Parameters.AddWithValue("$defaultServer", DefaultServerId);
            if (serverId > 0) command.Parameters.AddWithValue("$serverId", serverId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new DbCharacter
                {
                    Id = reader.GetInt64(0),
                    AccountId = accountId,
                    Name = reader.GetString(1),
                    Breed = reader.GetInt32(2),
                    Sex = reader.GetInt32(3),
                    Level = reader.GetInt32(4),
                    LookHex = reader.GetString(5),
                    ServerId = reader.GetInt32(6),
                    LastConnection = reader.GetString(7),
                    HeadId = reader.GetInt32(8)
                });
            }

            return list;
        }

        /// <summary>One character by id, or null when there is no such character.</summary>
        public static DbCharacter? GetCharacterById(long characterId)
        {
            if (characterId <= 0) return null;

            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText =
                "SELECT Id, AccountId, Name, Breed, Sex, Level, Look, " +
                "COALESCE(ServerId, $defaultServer), COALESCE(LastConnection, ''), " +
                "COALESCE(HeadId, 0) " +
                "FROM Characters WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", characterId);
            command.Parameters.AddWithValue("$defaultServer", DefaultServerId);

            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;

            return new DbCharacter
            {
                Id = reader.GetInt64(0),
                AccountId = reader.GetInt64(1),
                Name = reader.GetString(2),
                Breed = reader.GetInt32(3),
                Sex = reader.GetInt32(4),
                Level = reader.GetInt32(5),
                LookHex = reader.GetString(6),
                ServerId = reader.GetInt32(7),
                LastConnection = reader.GetString(8),
                HeadId = reader.GetInt32(9)
            };
        }

        /// <summary>
        /// Gives a head to the characters that have none. They predate the column, so the one
        /// their player picked is not recorded anywhere: each gets the first head the creation
        /// screen offers for its breed and sex, which is what the real client defaults to.
        /// </summary>
        private static void FillMissingHeads(SqliteConnection connection)
        {
            try
            {
                var pending = new List<(long Id, int Breed, int Sex)>();

                var query = connection.CreateCommand();
                query.CommandText =
                    "SELECT Id, Breed, Sex FROM Characters WHERE HeadId IS NULL OR HeadId = 0;";
                using (var reader = query.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        pending.Add((reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2)));
                    }
                }

                int assigned = 0;
                foreach (var (id, breed, sex) in pending)
                {
                    int headId = Managers.HeadTable.DefaultHeadId(breed, sex);
                    if (headId <= 0) continue;

                    var update = connection.CreateCommand();
                    update.CommandText = "UPDATE Characters SET HeadId = $head WHERE Id = $id;";
                    update.Parameters.AddWithValue("$head", headId);
                    update.Parameters.AddWithValue("$id", id);
                    update.ExecuteNonQuery();
                    assigned++;
                }

                if (assigned > 0)
                {
                    Console.WriteLine($"[SQLite] Migration: assigned a head to {assigned} character(s).");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] Could not assign the missing heads: {ex.Message}");
            }
        }

        /// <summary>Records when a character last entered the world.</summary>
        public static void TouchLastConnection(long characterId)
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "UPDATE Characters SET LastConnection = $now WHERE Id = $id;";
                command.Parameters.AddWithValue("$now",
                    DateTimeOffset.Now.ToString(Network.ConnectionProtocol.ConnectionDateFormat));
                command.Parameters.AddWithValue("$id", characterId);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager] Could not update the last connection: {ex.Message}");
            }
        }

        /// <summary>Checks that a character really does belong to the account asking for it.</summary>
        public static bool CharacterBelongsToAccount(long characterId, long accountId)
        {
            if (characterId <= 0 || accountId <= 0) return false;
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM Characters WHERE Id = $id AND AccountId = $accId;";
                command.Parameters.AddWithValue("$id", characterId);
                command.Parameters.AddWithValue("$accId", accountId);
                return Convert.ToInt64(command.ExecuteScalar() ?? 0L) > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager] Error checking character ownership: {ex.Message}");
                return false;
            }
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

                // A map the world data does not know is a map the client cannot load either: it
                // gets the jru, finds nothing, and the character never appears anywhere. It should
                // not be possible to be standing on one, but a character that got there once stays
                // there for good, so it is worth catching on the way in.
                if (MapManager.GetMapInfo(GameState.MapId) == null && MapManager.Maps.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[SQLite] {GameState.CharacterName} is on map {GameState.MapId}, " +
                                      "which is not in the world data. Sending it back to the start.");
                    Console.ResetColor();
                    GameState.MapId = StartingMap;
                    GameState.CellId = StartingCell;
                }
                GameState.Orientation = reader.IsDBNull(14) ? 1 : reader.GetInt32(14);
                GameState.Kamas = reader.IsDBNull(15) ? 0 : reader.GetInt64(15);

                // If the character predates the column, give it the minimum experience that
                // matches its level so the bar does not show up empty.
                long storedXp = reader.IsDBNull(16) ? 0 : reader.GetInt64(16);
                long levelFloor = ExperienceTable.LevelFloor(GameState.CharacterLevel);
                GameState.Experience = Math.Max(storedXp, levelFloor);
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

                // Los efectos se guardan como los manda el cliente, una lista de listas:
                // [[efecto, valor, dado, cara], ...]. Aquí se leían como si fueran un diccionario
                // {"138": 80}, que es OTRA cosa: System.Text.Json se atragantaba, el catch se
                // tragaba la excepción y TODOS los objetos se quedaban sin efectos. Con eso, la
                // suma del equipo valía cero para todo —potencia, daños, fuerza, crítico, PA y
                // PM—, y de ahí que el personaje peleara con 6 PA y 3 PM y pegase como si fuera
                // desnudo, mientras el panel del cliente sí enseñaba los objetos bien, porque ése
                // los lee por otro lado (Managers.Equipment.ParseEffects).
                //
                // Se lee con ese mismo parser, que es el que ya sabía la forma buena. La forma
                // vieja de diccionario se sigue admitiendo por si quedó algo guardado así.
                string jsonEffects = reader.IsDBNull(4) ? "" : reader.GetString(4);
                item.RawEffects = jsonEffects;
                if (!string.IsNullOrEmpty(jsonEffects))
                {
                    var parsed = Managers.Equipment.ParseEffects(jsonEffects);
                    if (parsed.Count > 0)
                    {
                        foreach (var effect in parsed)
                        {
                            // Un objeto puede repetir efecto; se suman, no se pisan.
                            item.Effects.TryGetValue(effect.Effect, out int already);
                            item.Effects[effect.Effect] = already + (int)effect.Value;
                        }
                    }
                    else
                    {
                        try
                        {
                            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, int>>(jsonEffects);
                            if (dict != null)
                            {
                                foreach (var kvp in dict) item.Effects[kvp.Key] = kvp.Value;
                            }
                        }
                        catch (Exception) { }
                    }
                }

                list.Add(item);
            }
            return list;
        }

        /// <summary>
        /// Los efectos de un objeto listos para guardar, en la forma que espera todo el mundo:
        /// [[efecto, valor, dado, cara], ...].
        ///
        /// Si el objeto vino de la base se devuelven tal cual llegaron, sin recomponerlos, para no
        /// perder los dados por el camino. Sólo se arma la lista cuando el objeto es nuevo.
        /// </summary>
        private static string EffectsForStorage(PlayerItem item)
        {
            if (!string.IsNullOrEmpty(item.RawEffects)) return item.RawEffects;

            var lista = new List<int[]>();
            foreach (var kvp in item.Effects) lista.Add(new[] { kvp.Key, kvp.Value, 0, 0 });
            return System.Text.Json.JsonSerializer.Serialize(lista);
        }

        public static void SaveInventoryItem(long characterId, PlayerItem item)
        {
            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            string jsonEffects = EffectsForStorage(item);

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

        /// <summary>¿Hay ya alguien con ese nombre? Los nombres son únicos en todo el servidor.</summary>
        public static bool CharacterNameTaken(string name)
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM Characters WHERE Name = $n COLLATE NOCASE;";
                command.Parameters.AddWithValue("$n", name);
                return command.ExecuteScalar() is long n && n > 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Crea un personaje con lo que trae puesto de fábrica: nivel 1, el conjunto del aventurero
        /// equipado, un millón de kamas y las características que dan los pergaminos.
        ///
        /// El identificador se saca del mayor que haya más uno. Los uid de sus objetos salen de un
        /// rango propio por personaje, para que no choquen con los de nadie.
        /// </summary>
        public static long CreateCharacter(long accountId, int serverId, string name, int breed,
                                           int sex, int headId, IReadOnlyList<long> colors,
                                           long mapId, int level, long kamas, int stat,
                                           IReadOnlyList<(int Gid, int Slot)> starterSet)
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();

                var siguiente = connection.CreateCommand();
                siguiente.CommandText = "SELECT IFNULL(MAX(Id), 1000000) + 1 FROM Characters;";
                long id = siguiente.ExecuteScalar() is long max ? max : 1000001;

                // La casilla: al lado del zaap, no encima.
                var zaap = Managers.Interactives.ZaapOf(mapId);
                int cell = MapManager.GetNearestWalkableCell(mapId, zaap.Cell);

                // Los colores que el cliente manda a -1 son "los de la raza"; se guardan vacíos y
                // BreedLookTable pone los suyos.
                var propios = new List<long>();
                foreach (long c in colors) if (c >= 0) propios.Add(c);
                byte[] look = Managers.BreedLookTable.BuildLook(breed, sex, headId,
                                                               propios.Count > 0 ? propios : null);

                using var transaction = connection.BeginTransaction();

                var insertar = connection.CreateCommand();
                insertar.CommandText = @"
                    INSERT INTO Characters
                        (Id, AccountId, Name, Breed, Sex, Level, MapId, CellId, RemainingPoints,
                         Vitality, Wisdom, Strength, Intelligence, Chance, Agility, Look,
                         Orientation, Kamas)
                    VALUES ($id, $acc, $name, $breed, $sex, $level, $map, $cell, 0,
                            $stat, $stat, $stat, $stat, $stat, $stat, $look, 1, $kamas);";
                insertar.Parameters.AddWithValue("$id", id);
                insertar.Parameters.AddWithValue("$acc", accountId);
                insertar.Parameters.AddWithValue("$name", name);
                insertar.Parameters.AddWithValue("$breed", breed);
                insertar.Parameters.AddWithValue("$sex", sex);
                insertar.Parameters.AddWithValue("$level", level);
                insertar.Parameters.AddWithValue("$map", mapId);
                insertar.Parameters.AddWithValue("$cell", cell);
                insertar.Parameters.AddWithValue("$stat", stat);
                // En hexadecimal, que es como la guardan los que ya estaban. En base64 el cargador
                // se atraganta: "Additional non-parsable characters are at the end of the string".
                insertar.Parameters.AddWithValue("$look", Convert.ToHexString(look));
                insertar.Parameters.AddWithValue("$kamas", kamas);
                insertar.ExecuteNonQuery();

                SetServerAndHead(connection, id, serverId, headId);

                long uid = id * 1000;
                foreach (var (gid, slot) in starterSet)
                {
                    var objeto = connection.CreateCommand();
                    objeto.CommandText = "INSERT INTO CharacterItems " +
                                         "(CharacterId, Uid, Gid, Quantity, Position, Effects) " +
                                         "VALUES ($id, $uid, $gid, 1, $pos, $e);";
                    objeto.Parameters.AddWithValue("$id", id);
                    objeto.Parameters.AddWithValue("$uid", uid++);
                    objeto.Parameters.AddWithValue("$gid", gid);
                    objeto.Parameters.AddWithValue("$pos", slot);
                    objeto.Parameters.AddWithValue("$e", EffectsOfTemplate(connection, gid));
                    objeto.ExecuteNonQuery();
                }

                transaction.Commit();
                return id;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] No se pudo crear el personaje: {ex.Message}");
                return 0;
            }
        }

        /// <summary>El servidor y la cara, que sí tienen columna propia.</summary>
        private static void SetServerAndHead(SqliteConnection connection, long characterId,
                                             int serverId, int headId)
        {
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE Characters SET ServerId = $s, HeadId = $h WHERE Id = $id;";
            command.Parameters.AddWithValue("$s", serverId);
            command.Parameters.AddWithValue("$h", headId);
            command.Parameters.AddWithValue("$id", characterId);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Los efectos de fábrica de una plantilla, en la forma que guarda CharacterItems.
        ///
        /// No están sueltos: la plantilla trae en su Data una lista de `rid` y cada uno apunta a una
        /// fila de ItemEffects con el efecto, el valor y el par de dados. Sin esto, el conjunto del
        /// aventurero saldría sin una sola característica.
        ///
        /// Del sombrero de aventurero salen 118 (fuerza), 126 (inteligencia), 119 (agilidad) y 123
        /// (suerte), los cuatro con dado 5, que es "de 1 a 5". Se le da el máximo, que es lo que un
        /// objeto de estreno debería llevar.
        /// </summary>
        private static string EffectsOfTemplate(SqliteConnection connection, int gid)
        {
            try
            {
                var leer = connection.CreateCommand();
                leer.CommandText = "SELECT Data FROM ItemTemplates WHERE Id = $gid;";
                leer.Parameters.AddWithValue("$gid", gid);
                if (leer.ExecuteScalar() is not string data) return "[]";

                using var doc = System.Text.Json.JsonDocument.Parse(data);
                if (!doc.RootElement.TryGetProperty("possibleEffects", out var posibles)) return "[]";
                if (!posibles.TryGetProperty("Array", out var lista)) return "[]";

                var salida = new List<string>();
                foreach (var entrada in lista.EnumerateArray())
                {
                    if (!entrada.TryGetProperty("rid", out var rid)) continue;

                    var efecto = connection.CreateCommand();
                    efecto.CommandText = "SELECT EffectId, DiceNum, DiceSide, Value FROM ItemEffects " +
                                         "WHERE Rid = $rid;";
                    efecto.Parameters.AddWithValue("$rid", rid.GetInt64());

                    using var reader = efecto.ExecuteReader();
                    if (!reader.Read()) continue;

                    int id = reader.GetInt32(0);
                    int diceNum = reader.GetInt32(1);
                    int diceSide = reader.GetInt32(2);
                    int value = reader.GetInt32(3);
                    if (id == 0) continue;

                    // El valor de estreno: el tope del dado si lo hay, y si no, el fijo.
                    int fijo = value != 0 ? value : (diceSide != 0 ? diceSide : diceNum);
                    salida.Add($"[{id},{fijo},0,0]");
                }
                return salida.Count > 0 ? "[" + string.Join(",", salida) + "]" : "[]";
            }
            catch { }
            return "[]";
        }

        /// <summary>
        /// Mete un objeto nuevo en el inventario de alguien. Lo usa la lotería del merkasako, que es
        /// lo único que fabrica objetos de la nada.
        /// </summary>
        public static bool InsertCharacterItem(long uid, long characterId, int gid, int quantity,
                                               int position, string? effects)
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO CharacterItems " +
                                      "(CharacterId, Uid, Gid, Quantity, Position, Effects) " +
                                      "VALUES ($id, $uid, $gid, $n, $pos, $e);";
                command.Parameters.AddWithValue("$id", characterId);
                command.Parameters.AddWithValue("$uid", uid);
                command.Parameters.AddWithValue("$gid", gid);
                command.Parameters.AddWithValue("$n", Math.Max(1, quantity));
                command.Parameters.AddWithValue("$pos", position);
                command.Parameters.AddWithValue("$e", (object?)effects ?? DBNull.Value);
                command.ExecuteNonQuery();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] No se pudo crear el objeto {uid}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Destruye un objeto del inventario, entero o por unidades. Devuelve si se ha hecho algo.
        /// </summary>
        public static bool DestroyCharacterItem(long characterId, long uid, int quantity)
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();

                var leer = connection.CreateCommand();
                leer.CommandText = "SELECT Quantity FROM CharacterItems WHERE Uid = $uid AND CharacterId = $id;";
                leer.Parameters.AddWithValue("$uid", uid);
                leer.Parameters.AddWithValue("$id", characterId);
                if (leer.ExecuteScalar() is not long tiene) return false;

                var command = connection.CreateCommand();
                if (quantity <= 0 || quantity >= tiene)
                {
                    command.CommandText = "DELETE FROM CharacterItems WHERE Uid = $uid AND CharacterId = $id;";
                }
                else
                {
                    command.CommandText = "UPDATE CharacterItems SET Quantity = Quantity - $n " +
                                          "WHERE Uid = $uid AND CharacterId = $id;";
                    command.Parameters.AddWithValue("$n", quantity);
                }
                command.Parameters.AddWithValue("$uid", uid);
                command.Parameters.AddWithValue("$id", characterId);
                return command.ExecuteNonQuery() > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] No se pudo destruir el objeto {uid}: {ex.Message}");
                return false;
            }
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
                    string jsonEffects = EffectsForStorage(item);
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

            // The Monsters.Spells column is empty for all 5134 monsters: the importer never filled
            // it in. The spells are in MonsterTemplates.Data though, along with the grade that
            // applies to each one. Without this the AI ended up with no spells and fell back to a
            // made-up one with range 6, so no monster ever had a reason to move.
            //
            //   spells      : {"Array":[626, 4195]}
            //   spellGrades : {"Array":["3,11;3,12;...", "1,11;1,12;..."]}
            //
            // spellGrades[i] describes the spell spells[i]: one "grade,level" entry per monster
            // grade, separated by ';'.
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
                Program.LogDebug($"[DatabaseManager] WARN: monster {monsterId} has no spells in Monsters nor in MonsterTemplates.");
            }

            return stats;
        }

        // Effect catalogue (Effects table, imported from data_assets_effectsdataroot). It says
        // which characteristic each effectId touches; without it nothing but damage can be applied.
        private static Dictionary<int, int>? _effectCharacteristics;

        /// <summary>
        /// De cada efecto, qué característica toca y con qué signo, según el catálogo del cliente.
        ///
        /// El signo sale de la DESCRIPCIÓN, no del BonusType. Parece más basto y es al revés: el
        /// BonusType no es de fiar. El 1079, que es el que roba PA —"-#1 a -#2 PA"—, lo tiene a
        /// CERO, igual que el 101; con esa regla Flecha Helada no robaba nada. La descripción, en
        /// cambio, es la plantilla con la que el propio cliente escribe el efecto en pantalla, y
        /// los que restan empiezan todos por un guion: el 1079, el 116 del alcance y el 169 de los
        /// PM.
        ///
        /// Y se dejan fuera los de categoría 2, que son los del ARMA: el 101 apunta a los puntos de
        /// acción, pero es lo que cuesta pegar con ella, no puntos que se ganen.
        /// </summary>
        public static (int Characteristic, int Sign) EffectMeta(int effectId)
        {
            LoadEffectCatalogue();
            return _effectMeta!.TryGetValue(effectId, out var meta) ? meta : (0, 0);
        }

        private static Dictionary<int, (int Characteristic, int Sign)>? _effectMeta;

        /// <summary>
        /// La FAMILIA de un efecto: su categoría y si es un bono, tal cual los declara el catálogo
        /// del cliente.
        ///
        /// Con estos dos números el cliente decide si un embrujo se pinta en el panel de efectos o
        /// si es maquinaria interna que no se enseña. Van en su propio diccionario, sin filtrar por
        /// característica, porque los que hacen falta aquí —el 950 que pone un estado, el 792 que
        /// encadena hechizos, el 293 de los daños básicos— no tienen característica propia.
        /// </summary>
        public static (int Category, int Boost) EffectFamily(int effectId)
        {
            if (_effectFamily == null)
            {
                var mapa = new Dictionary<int, (int, int)>();
                try
                {
                    using var conn = new SqliteConnection(WorldConnectionString);
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT Id, Category, Boost FROM Effects;";
                    using var rd = cmd.ExecuteReader();
                    while (rd.Read())
                    {
                        mapa[rd.GetInt32(0)] = (rd.IsDBNull(1) ? 0 : rd.GetInt32(1),
                                                rd.IsDBNull(2) ? 0 : rd.GetInt32(2));
                    }
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[DatabaseManager] No se pudo leer la familia de los efectos: {ex.Message}");
                }
                _effectFamily = mapa;
            }
            return _effectFamily.TryGetValue(effectId, out var familia) ? familia : (0, 0);
        }

        private static Dictionary<int, (int Category, int Boost)>? _effectFamily;

        /// <summary>
        /// De qué elemento pega un efecto, según el catálogo: 0 neutral, 1 tierra, 2 fuego, 3 agua
        /// y 4 aire. Menos uno cuando el efecto no pega de ningún elemento.
        /// </summary>
        public static int EffectElement(int effectId)
        {
            if (_effectElement == null)
            {
                var mapa = new Dictionary<int, int>();
                try
                {
                    using var conn = new SqliteConnection(WorldConnectionString);
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT Id, ElementId FROM Effects;";
                    using var rd = cmd.ExecuteReader();
                    while (rd.Read()) mapa[rd.GetInt32(0)] = rd.IsDBNull(1) ? -1 : rd.GetInt32(1);
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[DatabaseManager] No se pudo leer el elemento de los efectos: {ex.Message}");
                }
                _effectElement = mapa;
            }
            return _effectElement.TryGetValue(effectId, out int elemento) ? elemento : -1;
        }

        private static Dictionary<int, int>? _effectElement;

        /// <summary>La categoría de los efectos que describen el arma, no al personaje.</summary>
        private const int WeaponEffectCategory = 2;

        // ─── Los que ROBAN puntos ───────────────────────────────────────────────

        private static Dictionary<int, int>? _roboDePuntos;

        /// <summary>
        /// Qué característica roba un efecto de robo, o cero si no roba nada.
        ///
        /// Son una familia aparte y por eso se les hace un hueco: los cuatro —77 y 441 de puntos
        /// de movimiento, 84 y 440 de puntos de acción— llevan <c>Characteristic = 0</c> y
        /// <c>Category = 2</c> en la tabla, así que se los comían los dos filtros del catálogo
        /// general y no llegaban nunca al motor. El resultado en pantalla era que Flecha
        /// Inmovilizadora, en vez de quitarle un punto de movimiento al pío, le colgaba un
        /// embrujo llamado literalmente "Roba 1 PM" que no hacía nada.
        ///
        /// Cuál roban lo dice su propia descripción, que es de donde el catálogo ya saca el signo
        /// de los demás: "Roba #1 a #2 PM" contra "Roba #1 a #2 PA". No hay lista escrita a mano.
        /// </summary>
        public static int RoboDePuntos(int effectId)
        {
            if (_roboDePuntos == null)
            {
                var mapa = new Dictionary<int, int>();
                try
                {
                    using var conn = new SqliteConnection(WorldConnectionString);
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT Id, Description FROM Effects " +
                                      "WHERE Description LIKE 'Roba %';";
                    using var rd = cmd.ExecuteReader();
                    while (rd.Read())
                    {
                        string texto = rd.IsDBNull(1) ? "" : rd.GetString(1).TrimEnd();
                        if (texto.EndsWith("PM", StringComparison.Ordinal))
                            mapa[rd.GetInt32(0)] = MovementPointsCharacteristic;
                        else if (texto.EndsWith("PA", StringComparison.Ordinal))
                            mapa[rd.GetInt32(0)] = ActionPointsCharacteristic;
                    }
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[DatabaseManager] No se pudieron leer los robos de puntos: {ex.Message}");
                }
                _roboDePuntos = mapa;
            }
            return _roboDePuntos.TryGetValue(effectId, out int cual) ? cual : 0;
        }

        private const int ActionPointsCharacteristic = 1;
        private const int MovementPointsCharacteristic = 23;

        // ─── Los que MULTIPLICAN ────────────────────────────────────────────────

        private static HashSet<int>? _multiplicadores;

        /// <summary>
        /// Si un efecto multiplica en vez de sumar.
        /// </summary>
        /// <remarks>
        /// Se reconocen por su descripción, que es de la forma "… x#1%": el 1163 es "Daños
        /// sufridos x#1%" y el 1159 "Curas recibidas x#1%". Ninguno tiene característica en el
        /// catálogo, porque el cliente los resuelve por su número, y por eso no encajan en el
        /// camino corriente del motor.
        /// </remarks>
        public static bool EsMultiplicador(int effectId)
        {
            if (_multiplicadores == null)
            {
                var lista = new HashSet<int>();
                try
                {
                    using var conn = new SqliteConnection(WorldConnectionString);
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT Id FROM Effects WHERE Description LIKE '% x#1%';";
                    using var rd = cmd.ExecuteReader();
                    while (rd.Read()) lista.Add(rd.GetInt32(0));
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[DatabaseManager] No se pudieron leer los multiplicadores: {ex.Message}");
                }
                _multiplicadores = lista;
            }
            return _multiplicadores.Contains(effectId);
        }

        private static void LoadEffectCatalogue()
        {
            if (_effectMeta != null) return;
            var meta = new Dictionary<int, (int, int)>();
            try
            {
                using var conn = new SqliteConnection(WorldConnectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Id, Characteristic, Category, Description FROM Effects " +
                                  "WHERE Characteristic > 0;";
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    int category = rd.IsDBNull(2) ? 0 : rd.GetInt32(2);
                    if (category == WeaponEffectCategory) continue;

                    string description = rd.IsDBNull(3) ? "" : rd.GetString(3);
                    int sign = description.TrimStart().StartsWith("-") ? -1 : 1;
                    meta[rd.GetInt32(0)] = (rd.GetInt32(1), sign);
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[DatabaseManager] No se pudo leer el catálogo de efectos: {ex.Message}");
            }
            _effectMeta = meta;
        }

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
                    // No filtering by UseInFight: that column is 0 for exactly the effects we care
                    // about (1079, which removes AP, and 116, which removes range) and only 31
                    // rows in the whole table have it set to 1, so it does not mean "used in
                    // combat". With the filter in place, Frozen Arrow never took the 2 AP away.
                    cmd.CommandText = "SELECT Id, Characteristic FROM Effects WHERE Characteristic > 0;";
                    using var rd = cmd.ExecuteReader();
                    while (rd.Read()) map[rd.GetInt32(0)] = rd.IsDBNull(1) ? 0 : rd.GetInt32(1);
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[DatabaseManager] Could not load the Effects table: {ex.Message}");
                }
                _effectCharacteristics = map;
            }
            return _effectCharacteristics.TryGetValue(effectId, out int c) ? c : 0;
        }

        /// <summary>
        /// The equipped weapon, expressed as if it were a spell, so that a weapon hit goes down
        /// the same path as a regular cast.
        ///
        /// AP cost and range come from the item template; damage comes from the effects rolled on
        /// that particular instance. Effects 91-95 (steal) and 96-100 (damage) carry their element
        /// in the client's own effect table, so no hand-written mapping is needed.
        /// </summary>
        public static SpellCombatData? GetEquippedWeaponAsSpell(long characterId)
        {
            const int WeaponSlot = 1;
            var weapon = LoadInventory(characterId).FirstOrDefault(i => i.Position == WeaponSlot);
            if (weapon == null) return null;

            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Data FROM ItemTemplates WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", weapon.ItemId);
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

            // Damage of the equipped instance. Every effect carries its element in the Effects table.
            foreach (var kv in weapon.Effects)
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
        /// The spells the character has available at its level.
        ///
        /// SpellVariants.SpellIdsJson comes INTERLEAVED: base spell, its variant, base spell, its
        /// variant... (checked against the localized spell names, which alternate between a base
        /// arrow spell and its variant). We keep the even indices and filter by each spell's
        /// minimum level.
        ///
        /// Both the combat jvn and the roleplay shortcut bar use this, so the player sees the
        /// same list in either place.
        /// </summary>
        public static List<int> GetPlayerAvailableSpells(int breedId, int level)
        {
            return GetBreedSpellIds(breedId)
                .Where((_, index) => index % 2 == 0)
                .Where(id => GetSpellMinPlayerLevel(id) <= level)
                .ToList();
        }

        /// <summary>
        /// A monster's loot table, already resolved for the given grade.
        ///
        /// It comes from MonsterTemplates.Data → drops[], where every entry carries the item and
        /// its per-grade probability (percentDropForGrade1..5). Taking the Red Piwi as an example:
        /// red piwi feather at 100 %, sesame seeds at 18 %, pouch of lemons at 3 %.
        ///
        /// Entries with criteria (`hasCriterions`) are discarded, because they are conditional
        /// — quest or achievement drops — and their criteria language ("Qo=13820&amp;PO!19649&amp;…") is
        /// not implemented. Never dropping them is preferable to always dropping them.
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

                // percentDropForGrade1..5; grades above the fifth reuse the last one.
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
                Program.LogDebug($"[DatabaseManager] Error reading the loot table of monster {monsterId}: {ex.Message}");
            }
            return drops;
        }

        /// <summary>
        /// Puts an item into the inventory. If one of the same kind is already loose in the bag,
        /// it adds to that stack instead of creating another entry. Returns the resulting item.
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

            // Critical hit damage. It comes in its own effect list: Frozen Arrow does 12-14 water
            // normally and 15-17 on a critical, with the same -2 AP.
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

                            // The element is given by the effect itself (effectElement). It is the
                            // same number the client expects in the damage packet: spell 13425
                            // carries effectElement=2 and the official capture sends f25.f1=2.
                            // The switch on effectId is only used when the field is missing.
                            data.Element = e.TryGetProperty("effectElement", out var ee)
                                ? ee.GetInt32()
                                : effectId switch
                                {
                                    96 => 3,  // Water
                                    97 => 1,  // Earth
                                    98 => 4,  // Air
                                    99 => 2,  // Fire
                                    100 => 0, // Neutral
                                    _ => 0
                                };
                            continue; // keep going: a spell can deal damage and push as well
                        }

                        int dice = e.TryGetProperty("diceNum", out var dnum) ? dnum.GetInt32() : 0;
                        int dur = e.TryGetProperty("duration", out var dur0) ? dur0.GetInt32() : 0;

                        // 5 = push, 6 = pull; the number of cells travels in diceNum.
                        // Repelling Arrow (32426) is 98 (air 15-17) + 5 (push of 2).
                        if (effectId == 5 || effectId == 6)
                        {
                            if (dice > 0) data.PushDistance = effectId == 5 ? dice : -dice;
                            continue;
                        }

                        // Effect 293: raises the base damage of one specific spell for a few turns
                        // ("Frozen Arrow: +4 base damage - 3 turns"). The affected spell travels
                        // in diceNum, the bonus in value and the turns in duration.
                        if (effectId == 293)
                        {
                            int affectedSpellId = dice;
                            int bonus = e.TryGetProperty("value", out var val293) ? val293.GetInt32() : 0;
                            if (affectedSpellId > 0 && bonus != 0)
                            {
                                data.DamageBuffs.Add(new SpellDamageBuff
                                {
                                    SpellId = affectedSpellId,
                                    Bonus = bonus,
                                    Duration = dur > 0 ? dur : 1
                                });
                            }
                            continue;
                        }

                        // Any other effect that touches a characteristic of the target. The
                        // characteristic comes from the client's effect catalogue, not from a
                        // hand-written list: Frozen Arrow's 1079 removes AP (characteristic 1)
                        // and the piwi's 116 removes range (characteristic 19).
                        int characteristic = GetEffectCharacteristic(effectId);
                        if (characteristic > 0 && dice != 0)
                        {
                            data.StatEffects.Add(new SpellStatEffect
                            {
                                EffectId = effectId,
                                Characteristic = characteristic,
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
                // No made-up fallback: if the breed has no spells in the database that is a data
                // problem and it has to be visible, not papered over with another class's ids.
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[DatabaseManager][WARN] Breed {breedId} has no spells in SpellVariants. " +
                                  "The character will end up with no spells in combat.");
                Console.ResetColor();
            }
            return spells;
        }

        /// <summary>
        /// Minimum character level at which a spell unlocks, per SpellLevels.
        /// Returns 1 when there is no record, so that missing data does not hide spells.
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
                Console.WriteLine($"[DatabaseManager] Error reading MinPlayerLevel of spell {spellId}: {ex.Message}");
            }
            return 1;
        }

        /// <summary>
        /// El mapa donde están los vendedores: el que más NPC tiene colocados.
        ///
        /// Se busca en vez de escribirse a pelo. Hoy gana el Pueblo de Amakna (88212759) con 52
        /// filas en NpcSpawns contra una sola del siguiente, así que no hay empate que deshacer;
        /// pero si mañana se puebla otro mapa, el que salga de aquí será el bueno sin que haya que
        /// tocar el comando. Devuelve (0, 0) cuando no hay ningún NPC colocado.
        /// </summary>
        public static (long MapId, int Npcs) GetMapWithMostNpcSpawns()
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT MapId, COUNT(*) AS c FROM NpcSpawns " +
                    "GROUP BY MapId ORDER BY c DESC, MapId ASC LIMIT 1;";

                using var reader = command.ExecuteReader();
                if (reader.Read()) return (reader.GetInt64(0), reader.GetInt32(1));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager] No se pudo buscar el mapa con más NPC: {ex.Message}");
            }
            return (0, 0);
        }

        /// <summary>
        /// El nombre de una subzona, en el idioma del cliente.
        ///
        /// Va en dos saltos, igual que los nombres de hechizo: SubAreaTemplates guarda un JSON con
        /// un nameId dentro, y ese nameId es la clave de Translations. Sale vacío cuando no hay
        /// traducción, y quien llame decidirá qué enseñar en su lugar.
        /// </summary>
        public static string GetSubAreaName(int subAreaId)
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT Data FROM SubAreaTemplates WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", subAreaId);

                string? data = command.ExecuteScalar() as string;
                if (string.IsNullOrEmpty(data)) return "";

                using var doc = System.Text.Json.JsonDocument.Parse(data);
                if (!doc.RootElement.TryGetProperty("nameId", out var nameId)) return "";

                var translation = connection.CreateCommand();
                translation.CommandText = "SELECT Text FROM Translations WHERE Key = $key;";
                translation.Parameters.AddWithValue("$key", nameId.GetInt64().ToString());

                return translation.ExecuteScalar() as string ?? "";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager] No se pudo leer el nombre de la subzona " +
                                  $"{subAreaId}: {ex.Message}");
                return "";
            }
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

        /// <summary>Grade (level) of each of the monster's spells, keyed by spell id.</summary>
        public Dictionary<int, int> SpellGrades { get; set; } = new Dictionary<int, int>();
    }

    public class MonsterDrop
    {
        public int ObjectId { get; set; }
        /// <summary>Drop chance, as a percentage, for the monster's grade.</summary>
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
        public int Element { get; set; } = 0; // 0=Neutral, 1=Earth, 2=Fire, 3=Water, 4=Air

        /// <summary>
        /// Whether the spell requires line of sight (castTestLos in the client's data).
        /// </summary>
        public bool NeedsLineOfSight { get; set; } = true;

        /// <summary>Can only be cast in a straight line.</summary>
        public bool CastInLine { get; set; }

        public int MaxCastPerTurn { get; set; }

        /// <summary>Casts allowed per turn on the SAME target. 0 = no limit.</summary>
        public int MaxCastPerTarget { get; set; }

        /// <summary>
        /// Base critical hit chance, as a percentage. The critical granted by the equipment is
        /// added on top: Frozen Arrow brings 10 and the Turquoise Dofus another 10, which is
        /// where the 20 % shown in the spell description comes from.
        /// </summary>
        public int CriticalHitProbability { get; set; }

        public int CriticalDamageMin { get; set; }
        public int CriticalDamageMax { get; set; }
        public bool HasCriticalDamage => CriticalDamageMin > 0 || CriticalDamageMax > 0;

        /// <summary>Cells of displacement: positive pushes, negative pulls. 0 = does not move.</summary>
        public int PushDistance { get; set; }

        /// <summary>
        /// Effects that modify a characteristic of the target (removing AP, removing range,
        /// bonuses...). Each one already carries the characteristic it maps to according to the
        /// client's effect catalogue.
        /// </summary>
        public List<SpellStatEffect> StatEffects { get; set; } = new List<SpellStatEffect>();

        /// <summary>Base damage bonuses that this spell leaves in place once cast.</summary>
        public List<SpellDamageBuff> DamageBuffs { get; set; } = new List<SpellDamageBuff>();
    }

    public class SpellDamageBuff
    {
        /// <summary>The spell whose base damage goes up.</summary>
        public int SpellId { get; set; }
        public int Bonus { get; set; }
        /// <summary>How many turns it lasts from the moment it is applied.</summary>
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
