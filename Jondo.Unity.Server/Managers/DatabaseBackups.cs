using Jondo.Unity.Launcher;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jondo.Unity.Server
{
    /// <summary>
    /// Consistent SQLite snapshots taken before DatabaseManager is allowed to migrate anything.
    /// BackupDatabase includes committed WAL pages; copying only the .db file would not.
    /// </summary>
    public static class DatabaseBackups
    {
        public const int DefaultRetention = 5;

        public sealed class Result
        {
            public string Directory { get; init; } = "";
            public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
        }

        public static Result? CreateBeforeMigration()
        {
            var databases = new[] { Paths.AuthDb, Paths.WorldDb }
                .Where(File.Exists)
                .ToArray();
            if (databases.Length == 0)
            {
                Console.WriteLine("[SQLite] No existing databases to back up on first start.");
                return null;
            }

            try
            {
                Result result = Create(databases, Paths.DatabaseBackupsDir, DefaultRetention);
                Console.WriteLine($"[SQLite] Verified backup created in {result.Directory} " +
                                  $"({result.Files.Count} database(s), keeping {DefaultRetention}).");
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite][ERROR] Pre-migration backup failed: {ex.Message}");
                Console.WriteLine("[SQLite][ERROR] Database initialization cancelled to preserve existing data.");
                throw;
            }
        }

        /// <summary>Creates and verifies one backup set, then removes sets beyond retention.</summary>
        public static Result Create(IEnumerable<string> databases, string backupsDirectory,
                                    int retention = DefaultRetention,
                                    Func<DateTimeOffset>? clock = null)
        {
            if (databases == null) throw new ArgumentNullException(nameof(databases));
            if (string.IsNullOrWhiteSpace(backupsDirectory))
                throw new ArgumentException("A backup directory is required.", nameof(backupsDirectory));
            if (retention < 1) throw new ArgumentOutOfRangeException(nameof(retention));

            string root = Path.GetFullPath(backupsDirectory);
            Directory.CreateDirectory(root);

            DateTimeOffset now = (clock ?? (() => DateTimeOffset.UtcNow))().ToUniversalTime();
            string finalDirectory = UniqueDirectory(root, now.ToString("yyyyMMdd-HHmmss-fff"));
            string temporaryDirectory = finalDirectory + ".tmp";
            Directory.CreateDirectory(temporaryDirectory);

            var copied = new List<string>();
            var manifestFiles = new List<object>();
            try
            {
                foreach (string candidate in databases)
                {
                    string source = Path.GetFullPath(candidate);
                    if (!File.Exists(source)) continue;

                    string fileName = Path.GetFileName(source);
                    if (copied.Exists(path => string.Equals(Path.GetFileName(path), fileName,
                                                            StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException($"Two databases are named {fileName}.");

                    string destination = Path.Combine(temporaryDirectory, fileName);
                    BackupOne(source, destination);
                    if (!Verify(destination, out string check))
                        throw new InvalidDataException($"{fileName} failed PRAGMA quick_check: {check}");

                    copied.Add(destination);
                    var info = new FileInfo(source);
                    manifestFiles.Add(new
                    {
                        name = fileName,
                        bytes = new FileInfo(destination).Length,
                        sourceLastWriteUtc = info.LastWriteTimeUtc,
                        quickCheck = check,
                    });
                }

                if (copied.Count == 0)
                    throw new InvalidOperationException("None of the requested databases exists.");

                File.WriteAllText(Path.Combine(temporaryDirectory, "manifest.json"),
                    JsonSerializer.Serialize(new
                    {
                        createdUtc = now,
                        version = Contract.Version,
                        files = manifestFiles,
                    }, new JsonSerializerOptions { WriteIndented = true }));

                Directory.Move(temporaryDirectory, finalDirectory);
                Rotate(root, retention);

                return new Result
                {
                    Directory = finalDirectory,
                    Files = copied.Select(path => Path.Combine(finalDirectory, Path.GetFileName(path)))
                                  .ToArray(),
                };
            }
            catch
            {
                if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, true);
                throw;
            }
        }

        public static bool Verify(string database, out string result)
        {
            try
            {
                var builder = new SqliteConnectionStringBuilder
                {
                    DataSource = Path.GetFullPath(database),
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                };
                using var connection = new SqliteConnection(builder.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA quick_check;";
                result = Convert.ToString(command.ExecuteScalar()) ?? "no result";
                return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                result = ex.Message;
                return false;
            }
        }

        private static void BackupOne(string source, string destination)
        {
            var sourceBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = source,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            };
            var destinationBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = destination,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            };

            using var from = new SqliteConnection(sourceBuilder.ToString());
            using var to = new SqliteConnection(destinationBuilder.ToString());
            from.Open();
            to.Open();
            from.BackupDatabase(to);
        }

        private static string UniqueDirectory(string root, string stem)
        {
            string candidate = Path.Combine(root, stem);
            int suffix = 1;
            while (Directory.Exists(candidate) || Directory.Exists(candidate + ".tmp"))
                candidate = Path.Combine(root, $"{stem}-{suffix++:00}");
            return candidate;
        }

        private static void Rotate(string root, int retention)
        {
            string normalizedRoot = Path.GetFullPath(root);
            string prefix = Path.EndsInDirectorySeparator(normalizedRoot)
                ? normalizedRoot
                : normalizedRoot + Path.DirectorySeparatorChar;
            var sets = new DirectoryInfo(root).EnumerateDirectories()
                .Where(IsBackupSet)
                .OrderByDescending(directory => directory.Name, StringComparer.Ordinal)
                .ToList();

            foreach (var stale in sets.Skip(retention))
            {
                string target = Path.GetFullPath(stale.FullName);
                if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Backup path escaped its root: {target}");
                Directory.Delete(target, true);
            }
        }

        private static bool IsBackupSet(DirectoryInfo directory)
        {
            string name = directory.Name;
            if (name.Length != 19 && name.Length != 22) return false;
            if (name.Length == 22
                && (name[19] != '-' || !int.TryParse(name.AsSpan(20, 2), out _))) return false;

            return DateTime.TryParseExact(name.AsSpan(0, 19), "yyyyMMdd-HHmmss-fff",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _)
                && File.Exists(Path.Combine(directory.FullName, "manifest.json"));
        }
    }
}
