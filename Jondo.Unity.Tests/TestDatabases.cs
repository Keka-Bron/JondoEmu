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
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Tests] {Path.GetFileName(file)} could not be put in WAL: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// The server's settings at their defaults, whatever the machine's config/server_settings.json
        /// says: a raid minimum of one saved from the settings window made the raid board's test start
        /// a raid of three, which CI, with no such file, would never see.
        /// </summary>
        [ModuleInitializer]
        internal static void DefaultSettings() => Jondo.Unity.Server.ServerSettings.UseForTests(new Jondo.Unity.Server.ServerSettings());
    }
}
