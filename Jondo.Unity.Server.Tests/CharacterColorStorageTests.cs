using Jondo.Unity.Server.Database;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Server.Tests;

// Hand-built in-memory schema only: no migration or DatabaseManager.Initialize call.
public sealed class CharacterColorStorageTests
{
    [Fact]
    public void Both_character_queries_read_all_six_columns_and_keep_account_and_server_isolation()
    {
        using var connection = Open();
        var colors = new CharacterColors(null, 0, 0x112233, null, 0xFFFFFF, 0x445566);
        Insert(connection, 1, 71, 290, colors);
        Insert(connection, 2, 72, 290, default);
        Insert(connection, 3, 71, 291, new(1, 2, 3, 4, 5, 6));
        var listed = Assert.Single(DatabaseManager.GetCharactersByAccountId(connection, 71, 290));
        var selected = DatabaseManager.GetCharacterById(connection, 1)!;
        Assert.Equal(colors, listed.Colors);
        Assert.Equal(colors, selected.Colors);
        Assert.Equal((71L, 290, "Character-1", 18, 1, 57, 283),
            (selected.AccountId, selected.ServerId, selected.Name, selected.Breed, selected.Sex,
                selected.Level, selected.HeadId));
        Assert.Equal(2, DatabaseManager.GetCharactersByAccountId(connection, 71).Count);
        Assert.Empty(DatabaseManager.GetCharactersByAccountId(connection, 0));
        Assert.Null(DatabaseManager.GetCharacterById(connection, 99));
    }

    [Fact]
    public void Save_replaces_all_six_slots_without_touching_other_characters()
    {
        using var connection = Open();
        var original = new CharacterColors(1, 2, 3, 4, 5, 6);
        Insert(connection, 1, 71, 290, original);
        Insert(connection, 2, 72, 290, original);
        var chosen = new CharacterColors(null, 0, 0x112233, null, 0xFFFFFF, null);
        CharacterColorStorage.Save(connection, 1, chosen);
        var reloaded = DatabaseManager.GetCharacterById(connection, 1)!;
        Assert.Equal(chosen, reloaded.Colors);
        var other = DatabaseManager.GetCharacterById(connection, 2)!;
        Assert.Equal(original, other.Colors);
        Assert.Throws<InvalidOperationException>(() => CharacterColorStorage.Save(connection, 99, chosen));
    }

    [Fact]
    public void Storage_failure_does_not_leave_a_partial_colour_update()
    {
        using var connection = Open();
        var original = new CharacterColors(1, 2, 3, 4, 5, 6);
        Insert(connection, 1, 71, 290, original);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER RejectPalette BEFORE UPDATE ON Characters
            WHEN NEW.Color3 IS NULL
            BEGIN SELECT RAISE(ABORT, 'palette failed'); END;
            """;
        command.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => CharacterColorStorage.Save(connection, 1, default));
        var reloaded = DatabaseManager.GetCharacterById(connection, 1)!;
        Assert.Equal(original, reloaded.Colors);
    }

    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Characters (
                Id INTEGER PRIMARY KEY, AccountId INTEGER, ServerId INTEGER,
                Name TEXT, Breed INTEGER, Sex INTEGER, Level INTEGER, HeadId INTEGER,
                LastConnection TEXT,
                Color1 INTEGER, Color2 INTEGER, Color3 INTEGER,
                Color4 INTEGER, Color5 INTEGER, Color6 INTEGER
            );
            """;
        command.ExecuteNonQuery();
        return connection;
    }

    private static void Insert(SqliteConnection connection, long id, long account, int server, CharacterColors colors)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Characters
                (Id, AccountId, ServerId, Name, Breed, Sex, Level, HeadId, LastConnection,
                 Color1, Color2, Color3, Color4, Color5, Color6)
            VALUES ($id, $account, $server, $name, 18, 1, 57, 283, '',
                    $color1, $color2, $color3, $color4, $color5, $color6);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$name", $"Character-{id}");
        CharacterColorStorage.Bind(command, colors);
        command.ExecuteNonQuery();
    }
}
