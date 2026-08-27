using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Server;
using Xunit;

namespace Jondo.Unity.Tests.Security
{
    public class DatabaseBackupsTests
    {
        [Fact]
        public void A_backup_set_is_consistent_and_carries_a_manifest()
        {
            string root = TemporaryDirectory();
            try
            {
                string auth = CreateDatabase(root, "auth.db", "account");
                string world = CreateDatabase(root, "world.db", "character");
                var instant = new DateTimeOffset(2026, 8, 27, 12, 34, 56, 789, TimeSpan.Zero);

                var result = DatabaseBackups.Create(new[] { auth, world },
                    Path.Combine(root, "backups"), clock: () => instant);

                Assert.Equal("20260827-123456-789", Path.GetFileName(result.Directory));
                Assert.Equal(2, result.Files.Count);
                Assert.All(result.Files, file =>
                {
                    Assert.True(File.Exists(file));
                    Assert.True(DatabaseBackups.Verify(file, out string check), check);
                });

                string manifestPath = Path.Combine(result.Directory, "manifest.json");
                using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
                Assert.Equal(2, manifest.RootElement.GetProperty("files").GetArrayLength());
                Assert.Equal("2026-08-27T12:34:56.789+00:00",
                             manifest.RootElement.GetProperty("createdUtc").GetString());

                using var restored = new SqliteConnection("Pooling=False;Data Source=" +
                    Path.Combine(result.Directory, "world.db").Replace('\\', '/'));
                restored.Open();
                using var query = restored.CreateCommand();
                query.CommandText = "SELECT Value FROM Sample;";
                Assert.Equal("character", query.ExecuteScalar());
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void Rotation_keeps_only_the_newest_complete_sets()
        {
            string root = TemporaryDirectory();
            try
            {
                string database = CreateDatabase(root, "world.db", "value");
                string backups = Path.Combine(root, "backups");
                string unrelated = Path.Combine(backups, "manual-restore-notes");
                Directory.CreateDirectory(unrelated);
                File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "do not rotate this");

                for (int minute = 0; minute < 4; minute++)
                {
                    int captured = minute;
                    DatabaseBackups.Create(new[] { database }, backups, retention: 2,
                        clock: () => new DateTimeOffset(2026, 8, 27, 12, captured, 0, TimeSpan.Zero));
                }

                string[] sets = Directory.GetDirectories(backups, "2026*")
                    .Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray()!;
                Assert.Equal(new[] { "20260827-120200-000", "20260827-120300-000" }, sets);
                Assert.True(File.Exists(Path.Combine(unrelated, "keep.txt")));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void Committed_wal_pages_are_present_in_the_snapshot()
        {
            string root = TemporaryDirectory();
            try
            {
                string database = Path.Combine(root, "world.db");
                using (var writer = new SqliteConnection(
                    "Pooling=False;Data Source=" + database.Replace('\\', '/')))
                {
                    writer.Open();
                    using var command = writer.CreateCommand();
                    command.CommandText = "PRAGMA journal_mode=WAL; " +
                                          "CREATE TABLE Sample (Value TEXT NOT NULL); " +
                                          "INSERT INTO Sample VALUES ('from-wal');";
                    command.ExecuteNonQuery();
                    Assert.True(File.Exists(database + "-wal"));

                    var backup = DatabaseBackups.Create(new[] { database },
                        Path.Combine(root, "backups"));
                    using var restored = new SqliteConnection("Pooling=False;Data Source=" +
                        backup.Files.Single().Replace('\\', '/'));
                    restored.Open();
                    using var query = restored.CreateCommand();
                    query.CommandText = "SELECT Value FROM Sample;";
                    Assert.Equal("from-wal", query.ExecuteScalar());
                }
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void A_corrupt_source_never_leaves_a_complete_looking_backup()
        {
            string root = TemporaryDirectory();
            try
            {
                string corrupt = Path.Combine(root, "world.db");
                File.WriteAllText(corrupt, "this is not sqlite");
                string backups = Path.Combine(root, "backups");

                Assert.ThrowsAny<Exception>(() => DatabaseBackups.Create(new[] { corrupt }, backups));

                Assert.Empty(Directory.GetDirectories(backups));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static string TemporaryDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "jondo-backup-tests-" + Guid.NewGuid());
            Directory.CreateDirectory(path);
            return path;
        }

        private static string CreateDatabase(string root, string name, string value)
        {
            string path = Path.Combine(root, name);
            using var connection = new SqliteConnection("Pooling=False;Data Source=" + path.Replace('\\', '/'));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Sample (Value TEXT NOT NULL); INSERT INTO Sample VALUES ($value);";
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
            return path;
        }
    }
}
