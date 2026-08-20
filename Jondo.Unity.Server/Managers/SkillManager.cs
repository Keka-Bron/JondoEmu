using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Launcher.Managers
{
    public sealed class SkillDefinition
    {
        private readonly List<int> _craftableItems = new List<int>();
        private readonly List<int> _modifiableItemTypes = new List<int>();

        public int Id { get; init; }
        public long NameId { get; init; }
        public int ParentJobId { get; init; }
        public int ElementActionId { get; init; }
        public int LevelMin { get; init; }
        public int GatheredResourceItem { get; init; }
        public int Cursor { get; init; }
        public int Range { get; init; }
        public string UseAnimation { get; init; } = "";
        public bool UseRangeInClient { get; init; }
        public bool ClientDisplay { get; init; }
        public bool AvailableInHouse { get; init; }
        public bool AllowMarking { get; init; }
        public bool IsForgemagus { get; init; }
        public IReadOnlyList<int> CraftableItemIds => _craftableItems;
        public IReadOnlyList<int> ModifiableItemTypeIds => _modifiableItemTypes;

        internal List<int> MutableCraftableItems => _craftableItems;
        internal List<int> MutableModifiableItemTypes => _modifiableItemTypes;
        public bool IsGathering => GatheredResourceItem > 0;
    }

    /// <summary>Imports and indexes skills; it never interprets ElementActionId as an element type.</summary>
    public static class SkillManager
    {
        private static IReadOnlyDictionary<int, SkillDefinition> _byId =
            new Dictionary<int, SkillDefinition>();
        private static IReadOnlyDictionary<int, IReadOnlyList<SkillDefinition>> _byJob =
            new Dictionary<int, IReadOnlyList<SkillDefinition>>();

        public static int Count => _byId.Count;
        public static IEnumerable<SkillDefinition> All => _byId.Values;
        public static bool TryGet(int id, out SkillDefinition skill) => _byId.TryGetValue(id, out skill!);
        public static IReadOnlyList<SkillDefinition> ForJob(int jobId)
            => _byJob.TryGetValue(jobId, out var skills) ? skills : Array.Empty<SkillDefinition>();

        public static void Initialize()
        {
            ImportIfAvailable();
            LoadFromDatabase();
            Console.WriteLine($"[Skills] {_byId.Count} compétences chargées.");
        }

        private sealed class SourceSkill
        {
            public required SkillDefinition Skill { get; init; }
            public required List<int> Craftable { get; init; }
            public required List<int> Modifiable { get; init; }
        }

        private static void ImportIfAvailable()
        {
            string path = Paths.SkillsJson;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Skills] {path} absent; utilisation du catalogue déjà présent en base.");
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var rows = new List<SourceSkill>();
                foreach (var data in DofusDudeCatalog.Rows(document))
                {
                    rows.Add(new SourceSkill
                    {
                        Skill = new SkillDefinition
                        {
                            Id = DofusDudeCatalog.Int32(data, "id"),
                            NameId = DofusDudeCatalog.Int64(data, "nameId"),
                            ParentJobId = DofusDudeCatalog.Int32(data, "parentJobId"),
                            ElementActionId = DofusDudeCatalog.Int32(data, "elementActionId", -1),
                            LevelMin = DofusDudeCatalog.Int32(data, "levelMin"),
                            GatheredResourceItem = DofusDudeCatalog.Int32(data, "gatheredRessourceItem", -1),
                            Cursor = DofusDudeCatalog.Int32(data, "cursor"),
                            Range = DofusDudeCatalog.Int32(data, "range"),
                            UseAnimation = DofusDudeCatalog.Text(data, "useAnimation"),
                            UseRangeInClient = DofusDudeCatalog.Boolean(data, "useRangeInClient"),
                            ClientDisplay = DofusDudeCatalog.Boolean(data, "clientDisplay"),
                            AvailableInHouse = DofusDudeCatalog.Boolean(data, "availableInHouse"),
                            AllowMarking = DofusDudeCatalog.Boolean(data, "allowMarking"),
                            IsForgemagus = DofusDudeCatalog.Boolean(data, "isForgemagus"),
                        },
                        Craftable = DofusDudeCatalog.IntArray(data, "craftableItemIds"),
                        Modifiable = DofusDudeCatalog.IntArray(data, "modifiableItemTypeIds"),
                    });
                }
                if (rows.Count == 0) throw new InvalidOperationException("Le catalogue Skills est vide.");
                Import(rows);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Skills] Import annulé, catalogue en base conservé: {ex.Message}");
            }
        }

        private static void Import(List<SourceSkill> rows)
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            // Recipes can already reference Skills on an existing database. Update Skills in
            // place rather than deleting their parents; only the two owned arrays are replaced.
            foreach (string table in new[] { "SkillCraftableItems", "SkillModifiableItemTypes" })
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM {table};";
                delete.ExecuteNonQuery();
            }

            using var skill = connection.CreateCommand();
            skill.Transaction = transaction;
            skill.CommandText = @"INSERT INTO Skills
                (Id, NameId, ParentJobId, ElementActionId, LevelMin, GatheredResourceItem,
                 Cursor, Range, UseAnimation, UseRangeInClient, ClientDisplay, AvailableInHouse,
                 AllowMarking, IsForgemagus)
                VALUES($id,$name,$job,$action,$level,$resource,$cursor,$range,$animation,$useRange,
                       $display,$house,$marking,$mage)
                ON CONFLICT(Id) DO UPDATE SET
                    NameId=excluded.NameId,
                    ParentJobId=excluded.ParentJobId,
                    ElementActionId=excluded.ElementActionId,
                    LevelMin=excluded.LevelMin,
                    GatheredResourceItem=excluded.GatheredResourceItem,
                    Cursor=excluded.Cursor,
                    Range=excluded.Range,
                    UseAnimation=excluded.UseAnimation,
                    UseRangeInClient=excluded.UseRangeInClient,
                    ClientDisplay=excluded.ClientDisplay,
                    AvailableInHouse=excluded.AvailableInHouse,
                    AllowMarking=excluded.AllowMarking,
                    IsForgemagus=excluded.IsForgemagus;";
            foreach (string parameter in new[] { "$id", "$name", "$job", "$action", "$level",
                         "$resource", "$cursor", "$range", "$animation", "$useRange", "$display",
                         "$house", "$marking", "$mage" })
                skill.Parameters.Add(parameter, parameter == "$animation" ? SqliteType.Text : SqliteType.Integer);

            using var craftable = ChildInsert(connection, transaction, "SkillCraftableItems", "ItemId");
            using var modifiable = ChildInsert(connection, transaction, "SkillModifiableItemTypes", "ItemTypeId");
            foreach (var row in rows)
            {
                var s = row.Skill;
                object[] values = { s.Id, s.NameId, s.ParentJobId, s.ElementActionId, s.LevelMin,
                    s.GatheredResourceItem, s.Cursor, s.Range, s.UseAnimation, s.UseRangeInClient ? 1 : 0,
                    s.ClientDisplay ? 1 : 0, s.AvailableInHouse ? 1 : 0, s.AllowMarking ? 1 : 0,
                    s.IsForgemagus ? 1 : 0 };
                for (int i = 0; i < skill.Parameters.Count; i++) skill.Parameters[i].Value = values[i];
                skill.ExecuteNonQuery();
                InsertChildren(craftable, s.Id, row.Craftable);
                InsertChildren(modifiable, s.Id, row.Modifiable);
            }
            transaction.Commit();
        }

        private static SqliteCommand ChildInsert(SqliteConnection connection, SqliteTransaction transaction,
                                                  string table, string valueColumn)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"INSERT INTO {table}(SkillId, {valueColumn}, Position) VALUES($skill,$value,$position);";
            command.Parameters.Add("$skill", SqliteType.Integer);
            command.Parameters.Add("$value", SqliteType.Integer);
            command.Parameters.Add("$position", SqliteType.Integer);
            return command;
        }

        private static void InsertChildren(SqliteCommand command, int skillId, List<int> values)
        {
            for (int i = 0; i < values.Count; i++)
            {
                command.Parameters["$skill"].Value = skillId;
                command.Parameters["$value"].Value = values[i];
                command.Parameters["$position"].Value = i;
                command.ExecuteNonQuery();
            }
        }

        private static void LoadFromDatabase()
        {
            var skills = new Dictionary<int, SkillDefinition>();
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT Id,NameId,ParentJobId,ElementActionId,LevelMin,
                    GatheredResourceItem,Cursor,Range,UseAnimation,UseRangeInClient,ClientDisplay,
                    AvailableInHouse,AllowMarking,IsForgemagus FROM Skills ORDER BY Id;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var s = new SkillDefinition
                    {
                        Id=reader.GetInt32(0), NameId=reader.GetInt64(1), ParentJobId=reader.GetInt32(2),
                        ElementActionId=reader.GetInt32(3), LevelMin=reader.GetInt32(4),
                        GatheredResourceItem=reader.GetInt32(5), Cursor=reader.GetInt32(6), Range=reader.GetInt32(7),
                        UseAnimation=reader.GetString(8), UseRangeInClient=reader.GetInt32(9)!=0,
                        ClientDisplay=reader.GetInt32(10)!=0, AvailableInHouse=reader.GetInt32(11)!=0,
                        AllowMarking=reader.GetInt32(12)!=0, IsForgemagus=reader.GetInt32(13)!=0,
                    };
                    skills.Add(s.Id, s);
                }
            }
            LoadChildren(connection, skills, "SkillCraftableItems", true);
            LoadChildren(connection, skills, "SkillModifiableItemTypes", false);

            var byJob = new Dictionary<int, IReadOnlyList<SkillDefinition>>();
            var building = new Dictionary<int, List<SkillDefinition>>();
            foreach (var s in skills.Values)
            {
                if (!building.TryGetValue(s.ParentJobId, out var list)) building[s.ParentJobId] = list = new();
                list.Add(s);
            }
            foreach (var pair in building) byJob[pair.Key] = pair.Value;
            _byId = skills;
            _byJob = byJob;
        }

        private static void LoadChildren(SqliteConnection connection, Dictionary<int, SkillDefinition> skills,
                                         string table, bool craftable)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT SkillId, {(craftable ? "ItemId" : "ItemTypeId")} FROM {table} ORDER BY SkillId, Position;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!skills.TryGetValue(reader.GetInt32(0), out var skill)) continue;
                if (craftable) skill.MutableCraftableItems.Add(reader.GetInt32(1));
                else skill.MutableModifiableItemTypes.Add(reader.GetInt32(1));
            }
        }
    }
}
