using System;
using Jondo.Unity.Server.Database.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class ColorPaletteMigrationTests
{
    [Fact]
    public void Migration_creates_both_palette_tables_and_records_completion_marker()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        // Base schema before migration
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE JondoMigrations (Name TEXT PRIMARY KEY, AppliedAt TEXT NOT NULL);";
            cmd.ExecuteNonQuery();
        }

        Assert.True(ColorPalettePalettesMigration.Apply(connection));

        // Check tables were created
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='CharacterColorSlots';"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='CharacterColorPalettes';"));

        // Check completion marker
        Assert.Equal(1L, Scalar(connection, $"SELECT COUNT(*) FROM JondoMigrations WHERE Name = '{ColorPalettePalettesMigration.Name}';"));

        // Re-applying must be a clean no-op
        Assert.False(ColorPalettePalettesMigration.Apply(connection));
    }

    [Fact]
    public void Migration_failure_rolls_back_and_does_not_record_completion()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE JondoMigrations (Name TEXT PRIMARY KEY, AppliedAt TEXT NOT NULL);
                CREATE TRIGGER AbortMigration BEFORE INSERT ON JondoMigrations
                BEGIN SELECT RAISE(ABORT, 'forced test abort'); END;
                """;
            cmd.ExecuteNonQuery();
        }

        Assert.Throws<SqliteException>(() => ColorPalettePalettesMigration.Apply(connection));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM JondoMigrations;"));
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
