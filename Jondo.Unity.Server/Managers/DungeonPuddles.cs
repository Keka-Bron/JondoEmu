using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The colours a character has beaten in the run of a colour dungeon
    /// (<see cref="DungeonManager.ColourRooms"/>), and the boss rooms they open: the Gelatinous
    /// Dimension's puddles.
    /// </summary>
    /// <remarks>
    /// «Une fois le combat terminé, vous obtiendrez en inventaire une flaque de gelée de la couleur
    /// de la gelée majoritaire», then «vous affronterez la Gelée qui correspond à votre nombre le plus
    /// élevé de flaque», on a tie «vous pouvez choisir celle que vous voulez affronter parmi les deux»,
    /// and «Pour affronter les 4 Gelées Royales en même temps, il faudra avoir une flaque de chaque
    /// couleur de Gelée» (Dofus pour les noobs, «Gelaxième Dimension»). The client has no puddle
    /// item, so they are kept here, one per room won, and forgotten when the run starts again
    /// («Si vous quittez le donjon et que vous revenez, les flaques dans votre inventaire
    /// disparaissent») or the boss is beaten.
    /// </remarks>
    internal static class DungeonPuddles
    {
        internal static void EnsureTable(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS CharacterDungeonPuddles (
                    CharacterId INTEGER NOT NULL,
                    DungeonId INTEGER NOT NULL,
                    RoomMapId INTEGER NOT NULL,
                    Colour INTEGER NOT NULL,
                    PRIMARY KEY (CharacterId, DungeonId, RoomMapId)
                );";
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// The colour a won group leaves: the colour monster beaten the most times, or null when no
        /// colour was beaten or two came level.
        /// </summary>
        internal static int? Majority(DungeonManager.ColourRooms rule, IReadOnlyDictionary<int, int> beaten)
        {
            int? best = null;
            int most = 0;
            bool level = false;
            foreach (int colour in rule.Monsters)
            {
                int count = beaten.TryGetValue(colour, out int n) ? n : 0;
                if (count == 0) continue;
                if (count > most) { best = colour; most = count; level = false; }
                else if (count == most) level = true;
            }
            return level ? null : best;
        }

        /// <summary>
        /// The boss rooms these puddles open: the room of every colour held the most times (a tie
        /// leaves the choice), and the room of all of them together once each colour is held.
        /// </summary>
        internal static HashSet<long> Choice(DungeonManager.ColourRooms rule, IReadOnlyDictionary<int, int> puddles)
        {
            var open = new HashSet<long>();
            int most = rule.Monsters.Select(c => puddles.TryGetValue(c, out int n) ? n : 0).DefaultIfEmpty(0).Max();
            if (most == 0) return open;
            foreach (var (colour, room) in rule.Royals)
                if (puddles.TryGetValue(colour, out int n) && n == most) open.Add(room);
            if (rule.All != 0 && rule.Monsters.All(c => puddles.TryGetValue(c, out int n) && n > 0))
                open.Add(rule.All);
            return open;
        }

        /// <summary>
        /// Whether this character may be taken into that map: always, unless it is a boss room a
        /// colour dungeon leads to and his puddles do not open it.
        /// </summary>
        internal static bool Opens(long characterId, long map)
        {
            var dungeon = DungeonManager.ColouredTo(map);
            if (dungeon?.Colours == null) return true;
            return Choice(dungeon.Colours, Of(characterId, dungeon)).Contains(map);
        }

        /// <summary>
        /// After a won fight: a puddle of the beaten colour in a colour room, or, in a boss room the
        /// colours lead to, the run's puddles forgotten.
        /// </summary>
        internal static void AfterAWin(long characterId, long map, IReadOnlyDictionary<int, int> beaten)
        {
            if (characterId <= 0 || map == 0) return;
            if (DungeonManager.ColouredTo(map) is { } finished)
            {
                Forget(characterId, finished);
                return;
            }
            var dungeon = DungeonManager.OfRoom(map);
            if (dungeon?.Colours == null || !dungeon.Rooms.Contains(map)) return;
            if (Majority(dungeon.Colours, beaten) is not int colour) return;
            Record(characterId, dungeon, map, colour);
            Console.WriteLine($"[Dungeon] {dungeon.Name}: a puddle of colour {colour} from room " +
                              $"{dungeon.Rooms.IndexOf(map) + 1}.");
        }

        /// <summary>The puddles held in that dungeon's run: colour → how many.</summary>
        internal static Dictionary<int, int> Of(long characterId, DungeonManager.Dungeon dungeon)
        {
            var held = new Dictionary<int, int>();
            if (characterId <= 0) return held;
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Colour, COUNT(*) FROM CharacterDungeonPuddles " +
                                      "WHERE CharacterId = $character AND DungeonId = $dungeon GROUP BY Colour;";
                command.Parameters.AddWithValue("$character", characterId);
                command.Parameters.AddWithValue("$dungeon", dungeon.Id);
                using var reader = command.ExecuteReader();
                while (reader.Read()) held[(int)reader.GetInt64(0)] = (int)reader.GetInt64(1);
            }
            catch (SqliteException ex)
            {
                Console.WriteLine($"[Dungeon] Could not read the puddles of {characterId}: {ex.Message}");
            }
            return held;
        }

        internal static void Record(long characterId, DungeonManager.Dungeon dungeon, long room, int colour)
            => Run(characterId, dungeon,
                   "INSERT INTO CharacterDungeonPuddles (CharacterId, DungeonId, RoomMapId, Colour) " +
                   "VALUES ($character, $dungeon, $room, $colour) " +
                   "ON CONFLICT (CharacterId, DungeonId, RoomMapId) DO UPDATE SET Colour = $colour;",
                   ("$room", room), ("$colour", colour));

        /// <summary>A new run, or one finished: no puddle left.</summary>
        internal static void Forget(long characterId, DungeonManager.Dungeon dungeon)
            => Run(characterId, dungeon,
                   "DELETE FROM CharacterDungeonPuddles WHERE CharacterId = $character AND DungeonId = $dungeon;");

        private static void Run(long characterId, DungeonManager.Dungeon dungeon, string sql,
                                params (string Name, object Value)[] values)
        {
            if (characterId <= 0 || dungeon.Colours == null) return;
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("$character", characterId);
                command.Parameters.AddWithValue("$dungeon", dungeon.Id);
                foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
                command.ExecuteNonQuery();
            }
            catch (SqliteException ex)
            {
                Console.WriteLine($"[Dungeon] Could not update the puddles of {characterId}: {ex.Message}");
            }
        }
    }
}
