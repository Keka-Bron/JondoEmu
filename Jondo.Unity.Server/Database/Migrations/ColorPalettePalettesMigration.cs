using System;
using System.Data;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Database.Migrations;

public static class ColorPalettePalettesMigration
{
    public const string Name = "20261121-character-color-palettes";

    public static bool Apply(SqliteConnection connection)
    {
        if (connection.State != ConnectionState.Open)
            throw new InvalidOperationException("The database connection must already be open.");

        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'JondoMigrations';";
        if (command.ExecuteScalar() != null)
        {
            command.CommandText = "SELECT 1 FROM JondoMigrations WHERE Name = $migrationName;";
            command.Parameters.AddWithValue("$migrationName", Name);
            if (command.ExecuteScalar() != null)
            {
                transaction.Commit();
                return false;
            }
        }
        else
        {
            command.CommandText = "CREATE TABLE IF NOT EXISTS JondoMigrations (Name TEXT PRIMARY KEY, AppliedAt TEXT NOT NULL);";
            command.ExecuteNonQuery();
        }

        command.Parameters.Clear();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS CharacterColorSlots (
                CharacterId INTEGER PRIMARY KEY,
                SlotsUnlocked INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS CharacterColorPalettes (
                CharacterId INTEGER NOT NULL,
                PaletteIndex INTEGER NOT NULL,
                ColorsJson TEXT NOT NULL,
                PRIMARY KEY (CharacterId, PaletteIndex)
            );
            INSERT OR REPLACE INTO JondoMigrations (Name, AppliedAt)
            VALUES ($migrationName, $appliedAt);
            """;
        command.Parameters.AddWithValue("$migrationName", Name);
        command.Parameters.AddWithValue("$appliedAt", DateTime.UtcNow.ToString("o"));
        command.ExecuteNonQuery();

        transaction.Commit();
        return true;
    }
}
