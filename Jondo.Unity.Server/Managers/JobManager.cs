using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    public sealed class JobDefinition
    {
        public int Id { get; init; }
        public long NameId { get; init; }
        public int IconId { get; init; }
        public bool HasLegendaryCraft { get; init; }
    }

    /// <summary>Imports and indexes the 3.6.10.10 profession catalogue.</summary>
    public static class JobManager
    {
        private static IReadOnlyDictionary<int, JobDefinition> _byId =
            new Dictionary<int, JobDefinition>();

        public static int Count => _byId.Count;
        public static IEnumerable<JobDefinition> All => _byId.Values;
        public static bool TryGet(int id, out JobDefinition job) => _byId.TryGetValue(id, out job!);

        public static void Initialize()
        {
            ImportIfAvailable();
            LoadFromDatabase();
            Console.WriteLine($"[Jobs] {_byId.Count} oficios cargados.");
        }

        private static void ImportIfAvailable()
        {
            string path = Paths.JobsJson;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Jobs] Falta {path}; se usa el catalogo que ya hay en la base.");
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var rows = new List<JobDefinition>();
                foreach (var data in DofusDudeCatalog.Rows(document))
                {
                    rows.Add(new JobDefinition
                    {
                        Id = DofusDudeCatalog.Int32(data, "id"),
                        NameId = DofusDudeCatalog.Int64(data, "nameId"),
                        IconId = DofusDudeCatalog.Int32(data, "iconId", -1),
                        HasLegendaryCraft = DofusDudeCatalog.Boolean(data, "hasLegendaryCraft"),
                    });
                }
                if (rows.Count == 0) throw new InvalidOperationException("El catalogo de oficios esta vacio.");

                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var transaction = connection.BeginTransaction();
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = @"INSERT INTO Jobs(Id, NameId, IconId, HasLegendaryCraft)
                    VALUES($id, $name, $icon, $legendary)
                    ON CONFLICT(Id) DO UPDATE SET
                        NameId=excluded.NameId,
                        IconId=excluded.IconId,
                        HasLegendaryCraft=excluded.HasLegendaryCraft;";
                insert.Parameters.Add("$id", SqliteType.Integer);
                insert.Parameters.Add("$name", SqliteType.Integer);
                insert.Parameters.Add("$icon", SqliteType.Integer);
                insert.Parameters.Add("$legendary", SqliteType.Integer);
                foreach (var row in rows)
                {
                    insert.Parameters["$id"].Value = row.Id;
                    insert.Parameters["$name"].Value = row.NameId;
                    insert.Parameters["$icon"].Value = row.IconId;
                    insert.Parameters["$legendary"].Value = row.HasLegendaryCraft ? 1 : 0;
                    insert.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Jobs] Importacion cancelada, se conserva el catalogo de la base: {ex.Message}");
            }
        }

        private static void LoadFromDatabase()
        {
            var jobs = new Dictionary<int, JobDefinition>();
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, NameId, IconId, HasLegendaryCraft FROM Jobs ORDER BY Id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var job = new JobDefinition
                {
                    Id = reader.GetInt32(0),
                    NameId = reader.GetInt64(1),
                    IconId = reader.GetInt32(2),
                    HasLegendaryCraft = reader.GetInt32(3) != 0,
                };
                jobs.Add(job.Id, job);
            }
            _byId = jobs;
        }
    }
}
