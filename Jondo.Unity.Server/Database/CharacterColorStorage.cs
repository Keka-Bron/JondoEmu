using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Database;

/// <summary>Fixed-slot persistence. No protocol decoding or default resolution belongs here.</summary>
internal static class CharacterColorStorage
{
    internal static CharacterColors Read(SqliteDataReader reader, int firstColumn)
    {
        int? Color(int offset) => reader.IsDBNull(firstColumn + offset) ? null : reader.GetInt32(firstColumn + offset);
        return new(Color(0), Color(1), Color(2), Color(3), Color(4), Color(5));
    }

    internal static void Bind(SqliteCommand command, CharacterColors colors)
    {
        for (int slot = 1; slot <= 6; slot++)
            command.Parameters.AddWithValue($"$color{slot}", (object?)colors[slot] ?? DBNull.Value);
    }

    /// <summary>Replace all six colour choices in one atomic write.</summary>
    internal static void Save(SqliteConnection connection, long characterId, CharacterColors colors)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Characters
            SET Color1 = $color1, Color2 = $color2, Color3 = $color3,
                Color4 = $color4, Color5 = $color5, Color6 = $color6
            WHERE Id = $id;
            """;
        Bind(command, colors);
        command.Parameters.AddWithValue("$id", characterId);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"Character {characterId} no longer exists.");
    }
}
