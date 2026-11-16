using System;
using System.IO;
using System.Runtime.CompilerServices;
using Jondo.Unity.Launcher;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Tests
{
    /// <summary>
    /// The databases the tests share, in the journal mode the server itself puts them in.
    /// </summary>
    /// <remarks>
    /// A world.db fresh out of datos/world.zip -- the CI's -- is in rollback mode ("delete"),
    /// where a reader blocks every writer. With the test collections running in parallel, a
    /// write then waits out SQLite's 30 seconds and fails, a different test each time: five in
    /// one run against the zip's base, none against a base the server had opened, which is in
    /// WAL. The server sets WAL at start (DatabaseManager), so the tests start from it too.
    /// </remarks>
    internal static class TestDatabases
    {
        [ModuleInitializer]
        internal static void InWal()
        {
            foreach (var (file, connection) in new[] { (Paths.WorldDb, Paths.WorldConnectionString),
                                                       (Paths.AuthDb, Paths.AuthConnectionString) })
            {
                try
                {
                    if (!File.Exists(file)) continue;
                    using var db = new SqliteConnection(connection);
                    db.Open();
                    using var pragma = db.CreateCommand();
                    pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                    pragma.ExecuteNonQuery();
                    if (file == Paths.WorldDb)
                        Jondo.Unity.Server.Managers.DungeonProgress.EnsureTable(db);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Tests] {Path.GetFileName(file)} could not be put in WAL: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// The characters' colours in their own columns, as the server leaves them when it starts
        /// (DatabaseManager.Initialize): the zip's base -- the CI's -- still has the old Look
        /// column, and every test that creates a character failed on «no column named Color1».
        /// </summary>
        [ModuleInitializer]
        internal static void CharacterColorsMigrated()
        {
            try
            {
                if (!File.Exists(Paths.WorldDb)) return;
                using var db = new SqliteConnection(Paths.WorldConnectionString);
                db.Open();
                Jondo.Unity.Server.Database.Migrations.CharacterColorsMigration.Apply(db);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Tests] world.db's character colours could not be migrated: {ex.Message}");
            }
        }

        /// <summary>
        /// No traffic log for the tests: every frame a handler writes went into logs/ as «S->C», and a
        /// test run is enough of them to rotate the real sessions out of the files
        /// TrafficLogAgainstTheRealFileTests reads.
        /// </summary>
        [ModuleInitializer]
        internal static void NoTrafficLog() => Environment.SetEnvironmentVariable("JONDO_SIN_REGISTRO_DE_TRAFICO", "1");

        /// <summary>
        /// The server's settings at their defaults, whatever the machine's config/server_settings.json
        /// says: a raid minimum of one saved from the settings window made the raid board's test start
        /// a raid of three, which CI, with no such file, would never see.
        /// </summary>
        [ModuleInitializer]
        internal static void DefaultSettings() => Jondo.Unity.Server.ServerSettings.UseForTests(new Jondo.Unity.Server.ServerSettings());

        /// <summary>
        /// Lets go of the pooled connections of one temp database, so its file can be deleted.
        /// </summary>
        /// <remarks>
        /// Only that database's pool, never <c>SqliteConnection.ClearAllPools</c>: the collections
        /// run in parallel, and emptying every pool disposed the connection another collection was
        /// opening at that very moment -- "Cannot access a disposed object: SQLitePCL.sqlite3" in
        /// GuildStore.EnsureTables, a different guild test each time, once in a dozen runs.
        /// </remarks>
        internal static void ReleasePool(string file)
        {
            using var connection = new SqliteConnection($"Data Source={file}");
            SqliteConnection.ClearPool(connection);
        }
    }
}
