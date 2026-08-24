using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// Materializes query indexes from the immutable client_data snapshot into world.db.
    /// world.db is never authoritative for these tables: when the content fingerprint changes,
    /// they are rebuilt transactionally while player/account tables remain untouched.
    /// </summary>
    public static class StaticContentCache
    {
        public static void Synchronize(SqliteConnection connection)
        {
            EnsureSchema(connection);
            string fingerprint = Paths.StaticContentFingerprint;

            using (var read = connection.CreateCommand())
            {
                read.CommandText = "SELECT ClientVersion, ContentFingerprint FROM StaticContentState WHERE Id = 1;";
                using var row = read.ExecuteReader();
                if (row.Read())
                {
                    string cachedVersion = row.IsDBNull(0) ? "" : row.GetString(0);
                    string cachedFingerprint = row.IsDBNull(1) ? "" : row.GetString(1);
                    if (cachedVersion.Length > 0 &&
                        !string.Equals(cachedVersion, Paths.ActiveClientDataVersion, StringComparison.Ordinal))
                        throw new InvalidDataException(
                            $"world.db belongs to client {cachedVersion}; active client_data is {Paths.ActiveClientDataVersion}. " +
                            "Migrate mutable state into a version-specific database before changing protocol versions.");
                    if (string.Equals(cachedFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"[StaticData] world.db indexes match {Paths.ActiveClientDataVersion} ({fingerprint[..12]}...).");
                        return;
                    }
                }
            }

            Console.WriteLine($"[StaticData] Rebuilding immutable indexes from client_data/{Paths.ActiveClientDataVersion}...");
            using var transaction = connection.BeginTransaction();
            try
            {
                ClearStaticTables(connection, transaction);
                ImportItems(connection, transaction);
                ImportEffects(connection, transaction);
                ImportSpells(connection, transaction);
                ImportSpellLevels(connection, transaction);
                ImportSpellVariants(connection, transaction);
                ImportMonsters(connection, transaction);
                ImportSubareas(connection, transaction);
                ImportMaps(connection, transaction);
                ImportMapScrolls(connection, transaction);
                ImportNpcs(connection, transaction);

                using var state = Command(connection, transaction, @"
                    INSERT INTO StaticContentState (Id, ClientVersion, ContentFingerprint, UpdatedUtc)
                    VALUES (1, $version, $fingerprint, $utc)
                    ON CONFLICT(Id) DO UPDATE SET
                        ClientVersion = excluded.ClientVersion,
                        ContentFingerprint = excluded.ContentFingerprint,
                        UpdatedUtc = excluded.UpdatedUtc;");
                state.Parameters.AddWithValue("$version", Paths.ActiveClientDataVersion);
                state.Parameters.AddWithValue("$fingerprint", fingerprint);
                state.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
                state.ExecuteNonQuery();
                transaction.Commit();
                Console.WriteLine("[StaticData] JSON indexes rebuilt; mutable player state was preserved.");
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        private static void EnsureSchema(SqliteConnection connection)
        {
            using var schema = connection.CreateCommand();
            schema.CommandText = @"
                CREATE TABLE IF NOT EXISTS StaticContentState (
                    Id INTEGER PRIMARY KEY CHECK (Id = 1),
                    ClientVersion TEXT NOT NULL,
                    ContentFingerprint TEXT NOT NULL DEFAULT '',
                    UpdatedUtc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS ItemTemplates (Id INTEGER PRIMARY KEY, NameId INTEGER, Type INTEGER, Data TEXT);
                CREATE TABLE IF NOT EXISTS ItemEffects (Rid INTEGER PRIMARY KEY, EffectId INTEGER, DiceNum INTEGER, DiceSide INTEGER, Value INTEGER);
                CREATE TABLE IF NOT EXISTS Effects (
                    Id INTEGER PRIMARY KEY, Characteristic INTEGER, ElementId INTEGER, Category INTEGER,
                    BonusType INTEGER, Boost INTEGER, Active INTEGER, UseDice INTEGER, UseInFight INTEGER,
                    IsInPercent INTEGER, ForceMinMax INTEGER, EffectPriority INTEGER, DescriptionId INTEGER,
                    Description TEXT, CharacteristicOperator TEXT NOT NULL DEFAULT ''
                );
                CREATE TABLE IF NOT EXISTS MonsterTemplates (Id INTEGER PRIMARY KEY, NameId INTEGER, Look TEXT, Data TEXT);
                CREATE TABLE IF NOT EXISTS NpcTemplates (Id INTEGER PRIMARY KEY, NameId INTEGER, Look TEXT, Data TEXT);
                CREATE TABLE IF NOT EXISTS MapTemplates (Id INTEGER PRIMARY KEY, SubAreaId INTEGER, Data TEXT);
                CREATE TABLE IF NOT EXISTS MapPositions (
                    MapId INTEGER PRIMARY KEY, PosX INTEGER, PosY INTEGER, SubAreaId INTEGER,
                    Outdoor INTEGER, Name TEXT
                );
                CREATE TABLE IF NOT EXISTS MapScrolls (
                    MapId INTEGER PRIMARY KEY, RightMapId INTEGER, BottomMapId INTEGER,
                    LeftMapId INTEGER, TopMapId INTEGER
                );";
            schema.ExecuteNonQuery();
            EnsureColumn(connection, "StaticContentState", "ContentFingerprint", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, "Effects", "CharacteristicOperator", "TEXT NOT NULL DEFAULT ''");
            foreach (var column in new[]
            {
                ("Flags", "INTEGER NOT NULL DEFAULT 0"), ("CastInDiagonal", "INTEGER NOT NULL DEFAULT 0"),
                ("CastTestLos", "INTEGER NOT NULL DEFAULT 1"), ("NeedFreeCell", "INTEGER NOT NULL DEFAULT 0"),
                ("NeedTakenCell", "INTEGER NOT NULL DEFAULT 0"), ("HideEffects", "INTEGER NOT NULL DEFAULT 0"),
                ("CriticalHitProbability", "INTEGER NOT NULL DEFAULT 0"), ("InitialCooldown", "INTEGER NOT NULL DEFAULT 0"),
                ("GlobalCooldown", "INTEGER NOT NULL DEFAULT 0"), ("MinCastInterval", "INTEGER NOT NULL DEFAULT 0"),
                ("MaxStack", "INTEGER NOT NULL DEFAULT 0"), ("SpellBreed", "INTEGER NOT NULL DEFAULT 0"),
                ("StatesCriterion", "TEXT"), ("CriticalEffectsJson", "TEXT")
            }) EnsureColumn(connection, "SpellLevels", column.Item1, column.Item2);
        }

        private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
        {
            using var columns = connection.CreateCommand();
            columns.CommandText = $"PRAGMA table_info({table});";
            using var reader = columns.ExecuteReader();
            while (reader.Read()) if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
            reader.Close();
            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
            alter.ExecuteNonQuery();
        }

        private static void ClearStaticTables(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var clear = Command(connection, transaction, @"
                DELETE FROM ItemEffects; DELETE FROM ItemTemplates; DELETE FROM Effects;
                DELETE FROM SpellLevels; DELETE FROM Spells; DELETE FROM SpellVariants;
                DELETE FROM MonsterTemplates; DELETE FROM Monsters; DELETE FROM Subareas;
                DELETE FROM MapSubareas; DELETE FROM MapTemplates; DELETE FROM MapPositions;
                DELETE FROM MapScrolls; DELETE FROM NpcTemplates; DELETE FROM MapMobs;");
            clear.ExecuteNonQuery();
        }

        private static void ImportItems(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var templates = Command(connection, transaction,
                "INSERT INTO ItemTemplates (Id, NameId, Type, Data) VALUES ($id, $name, $type, $data);");
            Add(templates, "$id", SqliteType.Integer); Add(templates, "$name", SqliteType.Integer);
            Add(templates, "$type", SqliteType.Integer); Add(templates, "$data", SqliteType.Text);
            using var effects = Command(connection, transaction,
                "INSERT INTO ItemEffects (Rid, EffectId, DiceNum, DiceSide, Value) VALUES ($rid, $effect, $num, $side, $value);");
            Add(effects, "$rid", SqliteType.Integer); Add(effects, "$effect", SqliteType.Integer);
            Add(effects, "$num", SqliteType.Integer); Add(effects, "$side", SqliteType.Integer); Add(effects, "$value", SqliteType.Integer);
            int templateCount = 0, effectCount = 0;
            ForEachRow("itemsdataroot.json", (row, data) =>
            {
                int id = Int(data, "id");
                if (id > 0)
                {
                    Set(templates, "$id", id); Set(templates, "$name", Int(data, "nameId"));
                    Set(templates, "$type", Int(data, "typeId")); Set(templates, "$data", data.GetRawText());
                    templates.ExecuteNonQuery(); templateCount++;
                }
                else if (row.TryGetProperty("rid", out JsonElement rid) && rid.TryGetInt64(out long parsedRid) &&
                         data.TryGetProperty("effectId", out _))
                {
                    Set(effects, "$rid", parsedRid); Set(effects, "$effect", Int(data, "effectId"));
                    Set(effects, "$num", Int(data, "diceNum")); Set(effects, "$side", Int(data, "diceSide"));
                    Set(effects, "$value", Int(data, "value")); effects.ExecuteNonQuery(); effectCount++;
                }
            });
            Console.WriteLine($"[StaticData] {templateCount} item templates, {effectCount} item effects.");
        }

        private static void ImportEffects(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var command = Command(connection, transaction, @"
                INSERT INTO Effects (Id, Characteristic, ElementId, Category, BonusType, Boost, Active,
                    UseDice, UseInFight, IsInPercent, ForceMinMax, EffectPriority, DescriptionId, Description,
                    CharacteristicOperator)
                VALUES ($id,$characteristic,$element,$category,$bonus,$boost,$active,$dice,$fight,$percent,
                    $force,$priority,$descriptionId,$description,$operator);");
            foreach (string name in new[] { "$id", "$characteristic", "$element", "$category", "$bonus", "$boost", "$active", "$dice", "$fight", "$percent", "$force", "$priority", "$descriptionId" })
                Add(command, name, SqliteType.Integer);
            Add(command, "$description", SqliteType.Text);
            Add(command, "$operator", SqliteType.Text);
            int count = 0;
            ForEachRow("effectsdataroot.json", (_, data) =>
            {
                int id = Int(data, "id", -1); if (id < 0) return;
                Set(command, "$id", id); Set(command, "$characteristic", Int(data, "characteristic"));
                Set(command, "$element", Int(data, "elementId", -1)); Set(command, "$category", Int(data, "category"));
                Set(command, "$bonus", Int(data, "bonusType")); Set(command, "$boost", Flag(data, "boost"));
                Set(command, "$active", Flag(data, "active")); Set(command, "$dice", Flag(data, "useDice"));
                Set(command, "$fight", Flag(data, "useInFight")); Set(command, "$percent", Flag(data, "isInPercent"));
                Set(command, "$force", Flag(data, "forceMinMax")); Set(command, "$priority", Int(data, "effectPriority"));
                Set(command, "$descriptionId", Int(data, "descriptionId"));
                Set(command, "$description", "");
                Set(command, "$operator", Text(data, "characteristicOperator"));
                command.ExecuteNonQuery(); count++;
            });
            Console.WriteLine($"[StaticData] {count} effect definitions.");
        }

        private static void ImportSpells(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var command = Command(connection, transaction,
                "INSERT INTO Spells (Id, NameId, DescriptionId, IconId, TypeId) VALUES ($id,$name,$description,$icon,$type);");
            foreach (string name in new[] { "$id", "$name", "$description", "$icon", "$type" }) Add(command, name, SqliteType.Integer);
            int count = 0;
            ForEachRow("spellsdataroot.json", (_, data) =>
            {
                int id = Int(data, "id"); if (id <= 0) return;
                Set(command, "$id", id); Set(command, "$name", Int(data, "nameId"));
                Set(command, "$description", Int(data, "descriptionId")); Set(command, "$icon", Int(data, "iconId"));
                Set(command, "$type", Int(data, "typeId")); command.ExecuteNonQuery(); count++;
            });
            Console.WriteLine($"[StaticData] {count} spells.");
        }

        private static void ImportSpellLevels(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var command = Command(connection, transaction, @"
                INSERT INTO SpellLevels (Id, SpellId, Grade, MinPlayerLevel, APCost, MinRange, MaxRange,
                    CastInLine, MaxCastPerTurn, MaxCastPerTarget, EffectsJson, Flags, CastInDiagonal,
                    CastTestLos, NeedFreeCell, NeedTakenCell, HideEffects, CriticalHitProbability,
                    InitialCooldown, GlobalCooldown, MinCastInterval, MaxStack, SpellBreed, StatesCriterion,
                    CriticalEffectsJson)
                VALUES ($id,$spell,$grade,$level,$ap,$min,$max,$line,$turn,$target,$effects,$flags,$diagonal,
                    $los,$free,$taken,$hide,$critical,$initial,$global,$interval,$stack,$breed,$states,$criticalEffects);");
            foreach (string name in new[] { "$id", "$spell", "$grade", "$level", "$ap", "$min", "$max", "$line", "$turn", "$target", "$flags", "$diagonal", "$los", "$free", "$taken", "$hide", "$critical", "$initial", "$global", "$interval", "$stack", "$breed" })
                Add(command, name, SqliteType.Integer);
            foreach (string name in new[] { "$effects", "$states", "$criticalEffects" }) Add(command, name, SqliteType.Text);
            int count = 0;
            ForEachRow("spelllevelsdataroot.json", (_, data) =>
            {
                int id = Int(data, "id"); if (id <= 0) return;
                Set(command, "$id", id); Set(command, "$spell", Int(data, "spellId")); Set(command, "$grade", Int(data, "grade", 1));
                Set(command, "$level", Int(data, "minPlayerLevel")); Set(command, "$ap", Int(data, "apCost"));
                Set(command, "$min", Int(data, "minRange")); Set(command, "$max", Int(data, "range"));
                Set(command, "$line", Flag(data, "castInLine")); Set(command, "$turn", Int(data, "maxCastPerTurn"));
                Set(command, "$target", Int(data, "maxCastPerTarget")); Set(command, "$effects", RawArray(data, "effects"));
                Set(command, "$flags", Int(data, "m_flags")); Set(command, "$diagonal", Flag(data, "castInDiagonal"));
                Set(command, "$los", Flag(data, "castTestLos", 1)); Set(command, "$free", Flag(data, "needFreeCell"));
                Set(command, "$taken", Flag(data, "needTakenCell")); Set(command, "$hide", Flag(data, "hideEffects"));
                Set(command, "$critical", Int(data, "criticalHitProbability")); Set(command, "$initial", Int(data, "initialCooldown"));
                Set(command, "$global", Int(data, "globalCooldown")); Set(command, "$interval", Int(data, "minCastInterval"));
                Set(command, "$stack", Int(data, "maxStack")); Set(command, "$breed", Int(data, "spellBreed"));
                Set(command, "$states", Text(data, "statesCriterion")); Set(command, "$criticalEffects", RawArray(data, "criticalEffect"));
                command.ExecuteNonQuery(); count++;
            });
            Console.WriteLine($"[StaticData] {count} spell levels.");
        }

        private static void ImportSpellVariants(SqliteConnection connection, SqliteTransaction transaction)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Paths.SpellVariantsJson));
            if (!document.RootElement.TryGetProperty("references", out JsonElement references) ||
                !references.TryGetProperty("RefIds", out JsonElement rows) || rows.ValueKind != JsonValueKind.Array) return;
            var byBreed = new Dictionary<int, List<int>>();
            foreach (JsonElement row in rows.EnumerateArray())
            {
                if (!row.TryGetProperty("data", out JsonElement data)) continue;
                int breed = Int(data, "breedId"); if (breed <= 0) continue;
                if (!byBreed.TryGetValue(breed, out var spells)) byBreed[breed] = spells = new List<int>();
                if (data.TryGetProperty("spellIds", out JsonElement value))
                {
                    if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("Array", out JsonElement nested)) value = nested;
                    if (value.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement spell in value.EnumerateArray()) if (spell.TryGetInt32(out int id)) spells.Add(id);
                }
            }
            using var command = Command(connection, transaction,
                "INSERT INTO SpellVariants (BreedId, SpellIdsJson) VALUES ($breed,$spells);");
            Add(command, "$breed", SqliteType.Integer); Add(command, "$spells", SqliteType.Text);
            foreach (var entry in byBreed)
            {
                Set(command, "$breed", entry.Key); Set(command, "$spells", JsonSerializer.Serialize(entry.Value));
                command.ExecuteNonQuery();
            }
        }

        private static void ImportMonsters(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var runtime = Command(connection, transaction,
                "INSERT INTO Monsters (Id,NameId,Look,Grades,Spells) VALUES ($id,$name,$look,$grades,$spells);");
            using var templates = Command(connection, transaction,
                "INSERT INTO MonsterTemplates (Id,NameId,Look,Data) VALUES ($id,$name,$look,$data);");
            foreach (var command in new[] { runtime, templates })
            {
                Add(command, "$id", SqliteType.Integer); Add(command, "$name", SqliteType.Integer); Add(command, "$look", SqliteType.Text);
            }
            Add(runtime, "$grades", SqliteType.Text); Add(runtime, "$spells", SqliteType.Text); Add(templates, "$data", SqliteType.Text);
            int count = 0;
            ForEachRow("monstersdataroot.json", (_, data) =>
            {
                int id = Int(data, "id"); if (id <= 0) return;
                foreach (var command in new[] { runtime, templates })
                { Set(command, "$id", id); Set(command, "$name", Int(data, "nameId")); Set(command, "$look", Text(data, "look")); }
                Set(runtime, "$grades", RawArray(data, "grades")); Set(runtime, "$spells", RawArray(data, "spells"));
                Set(templates, "$data", data.GetRawText()); runtime.ExecuteNonQuery(); templates.ExecuteNonQuery(); count++;
            });
            Console.WriteLine($"[StaticData] {count} monsters.");
        }

        private static void ImportSubareas(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var command = Command(connection, transaction, "INSERT INTO Subareas (Id,Monsters) VALUES ($id,$monsters);");
            Add(command, "$id", SqliteType.Integer); Add(command, "$monsters", SqliteType.Text);
            ForEachRow("subareasdataroot.json", (_, data) =>
            {
                int id = Int(data, "id"); if (id <= 0) return;
                Set(command, "$id", id); Set(command, "$monsters", RawArray(data, "monsters")); command.ExecuteNonQuery();
            });
        }

        private static void ImportMaps(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var positions = Command(connection, transaction,
                "INSERT INTO MapPositions (MapId,PosX,PosY,SubAreaId,Outdoor,Name) VALUES ($id,$x,$y,$sub,$out,$name);");
            using var templates = Command(connection, transaction,
                "INSERT INTO MapTemplates (Id,SubAreaId,Data) VALUES ($id,$sub,$data);");
            using var subareas = Command(connection, transaction,
                "INSERT INTO MapSubareas (MapId,SubAreaId) VALUES ($id,$sub);");
            foreach (var command in new[] { positions, templates, subareas }) { Add(command, "$id", SqliteType.Integer); Add(command, "$sub", SqliteType.Integer); }
            Add(positions, "$x", SqliteType.Integer); Add(positions, "$y", SqliteType.Integer); Add(positions, "$out", SqliteType.Integer); Add(positions, "$name", SqliteType.Text);
            Add(templates, "$data", SqliteType.Text);
            int count = 0;
            ForEachRow("mapsinformationdataroot.json", (_, data) =>
            {
                long id = Long(data, "id"); if (id <= 0) return; int sub = Int(data, "subAreaId");
                foreach (var command in new[] { positions, templates, subareas }) { Set(command, "$id", id); Set(command, "$sub", sub); }
                Set(positions, "$x", Int(data, "posX")); Set(positions, "$y", Int(data, "posY"));
                Set(positions, "$out", Int(data, "worldMap", -1) >= 0 ? 1 : 0);
                // MapInformationData exposes nameId in MapTemplates.Data; localization remains a
                // client concern, so the legacy free-text cache label stays empty.
                Set(positions, "$name", "");
                Set(templates, "$data", data.GetRawText()); positions.ExecuteNonQuery(); templates.ExecuteNonQuery(); subareas.ExecuteNonQuery(); count++;
            });
            Console.WriteLine($"[StaticData] {count} map definitions.");
        }

        private static void ImportMapScrolls(SqliteConnection connection, SqliteTransaction transaction)
        {
            string path = Paths.ServerData("map_scrolls.json");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("clientVersion", out JsonElement version) ||
                version.GetString() != Paths.ActiveClientDataVersion ||
                !document.RootElement.TryGetProperty("rows", out JsonElement rows) || rows.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("map_scrolls.json is incompatible.");
            using var command = Command(connection, transaction,
                "INSERT INTO MapScrolls (MapId,RightMapId,BottomMapId,LeftMapId,TopMapId) VALUES ($id,$right,$bottom,$left,$top);");
            foreach (string name in new[] { "$id", "$right", "$bottom", "$left", "$top" }) Add(command, name, SqliteType.Integer);
            foreach (JsonElement data in rows.EnumerateArray())
            {
                Set(command, "$id", Long(data, "mapId")); Set(command, "$right", Long(data, "rightMapId"));
                Set(command, "$bottom", Long(data, "bottomMapId")); Set(command, "$left", Long(data, "leftMapId"));
                Set(command, "$top", Long(data, "topMapId")); command.ExecuteNonQuery();
            }
        }

        private static void ImportNpcs(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var command = Command(connection, transaction,
                "INSERT INTO NpcTemplates (Id,NameId,Look,Data) VALUES ($id,$name,$look,$data);");
            Add(command, "$id", SqliteType.Integer); Add(command, "$name", SqliteType.Integer);
            Add(command, "$look", SqliteType.Text); Add(command, "$data", SqliteType.Text);
            int count = 0;
            ForEachRow("npcsdataroot.json", (_, data) =>
            {
                int id = Int(data, "id"); if (id <= 0) return;
                Set(command, "$id", id); Set(command, "$name", Int(data, "nameId")); Set(command, "$look", Text(data, "look"));
                Set(command, "$data", data.GetRawText()); command.ExecuteNonQuery(); count++;
            });
            Console.WriteLine($"[StaticData] {count} NPC templates (placements remain server-owned evidence).");
        }

        private static void ForEachRow(string catalog, Action<JsonElement, JsonElement> action)
        {
            using var stream = File.OpenRead(Paths.Catalog(catalog));
            using JsonDocument document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("clientVersion", out JsonElement version) ||
                version.GetString() != Paths.ActiveClientDataVersion ||
                !document.RootElement.TryGetProperty("rows", out JsonElement rows) || rows.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"{catalog} is not a compatible normalized catalogue.");
            foreach (JsonElement row in rows.EnumerateArray())
                if (row.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object) action(row, data);
        }

        private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
        {
            var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command;
        }
        private static void Add(SqliteCommand command, string name, SqliteType type) => command.Parameters.Add(name, type);
        private static void Set(SqliteCommand command, string name, object value) => command.Parameters[name].Value = value;
        private static int Int(JsonElement value, string name, int fallback = 0)
            => value.TryGetProperty(name, out JsonElement item) && item.TryGetInt32(out int parsed) ? parsed : fallback;
        private static long Long(JsonElement value, string name, long fallback = 0)
            => value.TryGetProperty(name, out JsonElement item) && item.TryGetInt64(out long parsed) ? parsed : fallback;
        private static int Flag(JsonElement value, string name, int fallback = 0)
        {
            if (!value.TryGetProperty(name, out JsonElement item)) return fallback;
            if (item.ValueKind == JsonValueKind.True) return 1;
            if (item.ValueKind == JsonValueKind.False) return 0;
            return item.TryGetInt32(out int parsed) ? (parsed == 0 ? 0 : 1) : fallback;
        }
        private static string Text(JsonElement value, string name)
            => value.TryGetProperty(name, out JsonElement item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
        private static string RawArray(JsonElement value, string name)
        {
            if (!value.TryGetProperty(name, out JsonElement item)) return "[]";
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("Array", out JsonElement nested)) item = nested;
            return item.ValueKind == JsonValueKind.Array ? item.GetRawText() : "[]";
        }
    }
}
