using System;
using System.Data;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Database.Migrations;

/// <summary>
/// The colour palettes of the appearance window: how many slots each character has unlocked with
/// colour change potions (CharacterColorSlots) and the six colours saved in each slot
/// (CharacterColorPalettes). A character without rows has one slot and no saved palette.
/// </summary>
internal static class ColorPalettePalettesMigration
{
    internal const string Name = "20261121-character-color-palettes";

    /// <summary>Applies to an open world connection; false when already recorded.</summary>
    internal static bool Apply(SqliteConnection connection)
    {
        if (connection.State != ConnectionState.Open)
            throw new InvalidOperationException("The database connection must already be open.");

        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS JondoMigrations (Name TEXT PRIMARY KEY, AppliedAt TEXT NOT NULL);
            SELECT 1 FROM JondoMigrations WHERE Name = $migrationName;";
        command.Parameters.AddWithValue("$migrationName", Name);
        if (command.ExecuteScalar() != null)
        {
            transaction.Commit();
            return false;
        }

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
            INSERT INTO JondoMigrations (Name, AppliedAt) VALUES ($migrationName, $appliedAt);
            """;
        command.Parameters.AddWithValue("$appliedAt", DateTime.UtcNow.ToString("o"));
        command.ExecuteNonQuery();

        transaction.Commit();
        return true;
    }
}
