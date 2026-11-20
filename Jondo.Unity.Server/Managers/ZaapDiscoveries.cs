using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>The ordinary zaaps each character has visited.</summary>
    internal static class ZaapDiscoveries
    {
        internal static void EnsureTable(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS CharacterZaapDiscoveries (
                    CharacterId INTEGER NOT NULL,
                    MapId INTEGER NOT NULL,
                    PRIMARY KEY (CharacterId, MapId)
                );
                """;
            command.ExecuteNonQuery();
        }

        internal static IEnumerable<long> KnownMaps(long characterId)
        {
            var discovered = ServerSettings.Current.RequireZaapDiscovery ? Read(characterId) : null;
            return Interactives.Waypoints
                .Where(waypoint => waypoint.Activated &&
                    (discovered == null || discovered.Contains(waypoint.MapId)) &&
                    HasTravelZaap(waypoint.MapId))
                .Select(waypoint => waypoint.MapId);
        }

        private static bool HasTravelZaap(long mapId)
            => Interactives.ZaapElements(mapId).Exists(element => !Interactives.IsVestige(mapId, element));

        private static HashSet<long> Read(long characterId)
        {
            var known = new HashSet<long>();
            if (characterId <= 0) return known;

            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT MapId FROM CharacterZaapDiscoveries WHERE CharacterId = $character;";
                command.Parameters.AddWithValue("$character", characterId);
                using var reader = command.ExecuteReader();
                while (reader.Read()) known.Add(reader.GetInt64(0));
            }
            catch (SqliteException ex)
            {
                Console.WriteLine($"[Zaap] Could not read discoveries for character {characterId}: {ex.Message}");
            }

            return known;
        }

        internal static bool Discover(long characterId, long mapId)
        {
            if (characterId <= 0 || Interactives.WaypointOf(mapId) is not { Activated: true } ||
                !HasTravelZaap(mapId)) return false;

            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT OR IGNORE INTO CharacterZaapDiscoveries (CharacterId, MapId)
                    SELECT Id, $map FROM Characters WHERE Id = $character;
                    """;
                command.Parameters.AddWithValue("$character", characterId);
                command.Parameters.AddWithValue("$map", mapId);
                return command.ExecuteNonQuery() > 0;
            }
            catch (SqliteException ex)
            {
                Console.WriteLine($"[Zaap] Could not record map {mapId} for character {characterId}: {ex.Message}");
                return false;
            }
        }
    }
}
