using System;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    internal static class DungeonProgress
    {
        internal static void EnsureTable(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS CharacterDungeonProgress (
                    CharacterId INTEGER NOT NULL,
                    DungeonId INTEGER NOT NULL,
                    RoomMapId INTEGER NOT NULL,
                    PRIMARY KEY (CharacterId, DungeonId)
                );";
            command.ExecuteNonQuery();
        }

        internal static long SavedRoom(long characterId, DungeonManager.Dungeon dungeon)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT RoomMapId FROM CharacterDungeonProgress " +
                                      "WHERE CharacterId = $character AND DungeonId = $dungeon;";
                command.Parameters.AddWithValue("$character", characterId);
                command.Parameters.AddWithValue("$dungeon", dungeon.Id);
                return command.ExecuteScalar() is long room && dungeon.Rooms.Contains(room) ? room : 0;
            }
            catch (SqliteException ex)
            {
                Console.WriteLine($"[Dungeon] Could not read progress for {characterId}: {ex.Message}");
                return 0;
            }
        }

        internal static void SetRoom(long characterId, DungeonManager.Dungeon dungeon, long room)
        {
            if (characterId <= 0 || (room != 0 && !dungeon.Rooms.Contains(room))) return;

            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = room == 0
                    ? "DELETE FROM CharacterDungeonProgress WHERE CharacterId = $character AND DungeonId = $dungeon;"
                    : "INSERT INTO CharacterDungeonProgress (CharacterId, DungeonId, RoomMapId) " +
                      "VALUES ($character, $dungeon, $room) " +
                      "ON CONFLICT (CharacterId, DungeonId) DO UPDATE SET RoomMapId = $room;";
                command.Parameters.AddWithValue("$character", characterId);
                command.Parameters.AddWithValue("$dungeon", dungeon.Id);
                if (room != 0) command.Parameters.AddWithValue("$room", room);
                command.ExecuteNonQuery();
            }
            catch (SqliteException ex)
            {
                Console.WriteLine($"[Dungeon] Could not update progress for {characterId}: {ex.Message}");
            }
        }
    }
}
