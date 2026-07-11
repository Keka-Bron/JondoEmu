using System;
using System.IO;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Google.Protobuf;
using Jondo.Unity.Protocol.Messages;

namespace Jondo.Unity.Launcher
{
    public static class DatabaseManager
    {
        private static readonly string AuthConnectionString = "Data Source=C:/Jondo/auth.db";
        public static readonly string WorldConnectionString = "Data Source=C:/Jondo/world.db";

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

            // 2. Initialize world.db
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
                        Orientation INTEGER NOT NULL DEFAULT 1
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
                            13825558, 188940901, $name, 8, 1, 2, 154011397, 386, 
                            5, 0, 0, 0, 0, 0, 0, $look, 0
                        );
                    ";
                    seedChar.Parameters.AddWithValue("$name", "CADERNIS");
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

                // 4. Initialize Monsters tables
                var createMonsters = worldConnection.CreateCommand();
                createMonsters.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Monsters (
                        Id INTEGER PRIMARY KEY,
                        NameId INTEGER,
                        Look TEXT,
                        Grades TEXT
                    );
                    CREATE TABLE IF NOT EXISTS Subareas (
                        Id INTEGER PRIMARY KEY,
                        Monsters TEXT
                    );
                    CREATE TABLE IF NOT EXISTS MapSubareas (
                        MapId INTEGER PRIMARY KEY,
                        SubAreaId INTEGER
                    );
                ";
                createMonsters.ExecuteNonQuery();

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
                }
            }

            Console.WriteLine("[SQLite] Databases initialized successfully.");
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
                SELECT Name, Level, MapId, CellId, RemainingPoints, Vitality, Wisdom, Strength, Intelligence, Chance, Agility, Look, Breed, Sex, Orientation, Kamas
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
                    Level = $lvl, Kamas = $kamas
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
                string monstersPath = @"..\dofus3_data\monsters.json";
                if (File.Exists(monstersPath))
                {
                    using var fs = new FileStream(monstersPath, FileMode.Open, FileAccess.Read);
                    var doc = System.Text.Json.JsonDocument.Parse(fs);
                    var mValuesArr = doc.RootElement.GetProperty("objectsById").GetProperty("m_values").GetProperty("Array");
                    var mKeysArr = doc.RootElement.GetProperty("objectsById").GetProperty("m_keys").GetProperty("Array");
                    
                    var insertCmd = connection.CreateCommand();
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = "INSERT INTO Monsters (Id, NameId, Look, Grades) VALUES ($id, $nameId, $look, $grades);";
                    insertCmd.Parameters.Add("$id", SqliteType.Integer);
                    insertCmd.Parameters.Add("$nameId", SqliteType.Integer);
                    insertCmd.Parameters.Add("$look", SqliteType.Text);
                    insertCmd.Parameters.Add("$grades", SqliteType.Text);

                    for(int i = 0; i < mKeysArr.GetArrayLength(); i++)
                    {
                        var monsterId = mKeysArr[i].GetInt32();
                        var data = mValuesArr[i].GetProperty("data");
                        int nameId = data.TryGetProperty("nameId", out var nid) ? nid.GetInt32() : 0;
                        string look = data.TryGetProperty("look", out var lk) ? lk.GetString() : "";
                        string grades = data.TryGetProperty("grades", out var gr) ? gr.GetRawText() : "[]";

                        insertCmd.Parameters["$id"].Value = monsterId;
                        insertCmd.Parameters["$nameId"].Value = nameId;
                        insertCmd.Parameters["$look"].Value = look ?? "";
                        insertCmd.Parameters["$grades"].Value = grades;
                        insertCmd.ExecuteNonQuery();
                    }
                }

                // Subareas
                string subareasPath = @"..\dofus3_data\subareas.json";
                if (File.Exists(subareasPath))
                {
                    using var fs = new FileStream(subareasPath, FileMode.Open, FileAccess.Read);
                    var doc = System.Text.Json.JsonDocument.Parse(fs);
                    var mValuesArr = doc.RootElement.GetProperty("objectsById").GetProperty("m_values").GetProperty("Array");
                    var mKeysArr = doc.RootElement.GetProperty("objectsById").GetProperty("m_keys").GetProperty("Array");
                    
                    var insertCmd = connection.CreateCommand();
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = "INSERT INTO Subareas (Id, Monsters) VALUES ($id, $monsters);";
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
                string mapsPath = @"..\dofus3_data\maps_information.json";
                if (File.Exists(mapsPath))
                {
                    using var fs = new FileStream(mapsPath, FileMode.Open, FileAccess.Read);
                    var doc = System.Text.Json.JsonDocument.Parse(fs);
                    var mValuesArr = doc.RootElement.GetProperty("objectsById").GetProperty("m_values").GetProperty("Array");
                    var mKeysArr = doc.RootElement.GetProperty("objectsById").GetProperty("m_keys").GetProperty("Array");
                    
                    var insertCmd = connection.CreateCommand();
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = "INSERT INTO MapSubareas (MapId, SubAreaId) VALUES ($id, $subid);";
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
    }
}
