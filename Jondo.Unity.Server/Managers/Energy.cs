using System;
using System.Collections.Concurrent;
using Jondo.Unity.World.Fights;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// A character's energy: characteristic 29, <c>energyPoints</c>, out of the 10,000 of 47,
    /// <c>maxEnergyPoints</c>. What a lost fight spends -- see <see cref="DefeatPenalty"/>.
    /// </summary>
    /// <remarks>
    /// Kept in world.db, in a table of its own created at first use -- datos/world.zip, the base CI
    /// tests against, has never heard of it --, one row per character that has lost any: a
    /// character without a row has the full gauge, which is what every captured character
    /// fresh from rest carries (10000 in the kub of the Pandala capture).
    ///
    ///   CharacterEnergy (CharacterId, Energy)
    ///
    /// NOT DONE: getting it back. The help text says it returns with rest and consumables --
    /// "durante tu tiempo de desconexión", twice as fast in a house, a tavern or the class
    /// temple -- and the captures show it back at 10,000 a week later, but no capture measures
    /// the rate and the consumables are not in this change.
    /// </remarks>
    public static class Energy
    {
        private static readonly ConcurrentDictionary<long, int> _known = new();
        private static readonly object _tableLock = new object();
        private static volatile bool _tableReady;

        /// <summary>The character's energy: the stored figure, or the full gauge when there is none.</summary>
        public static int Of(long characterId)
        {
            if (characterId == 0) return DefeatPenalty.MaxEnergy;
            return _known.GetOrAdd(characterId, Read);
        }

        /// <summary>Stores the character's energy, within the gauge.</summary>
        public static void Set(long characterId, int energy)
        {
            if (characterId == 0) return;
            int value = Math.Clamp(energy, 0, DefeatPenalty.MaxEnergy);
            _known[characterId] = value;
            try
            {
                EnsureTable();
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO CharacterEnergy (CharacterId, Energy) VALUES ($c, $e) " +
                                      "ON CONFLICT(CharacterId) DO UPDATE SET Energy = excluded.Energy;";
                command.Parameters.AddWithValue("$c", characterId);
                command.Parameters.AddWithValue("$e", value);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Energy] Could not store the energy of {characterId}: {ex.Message}");
            }
        }

        /// <summary>For the tests: forget what was read, so the next read goes to the base.</summary>
        internal static void Forget(long characterId) => _known.TryRemove(characterId, out _);

        private static int Read(long characterId)
        {
            try
            {
                EnsureTable();
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Energy FROM CharacterEnergy WHERE CharacterId = $c;";
                command.Parameters.AddWithValue("$c", characterId);
                return command.ExecuteScalar() is long stored
                    ? (int)Math.Clamp(stored, 0, DefeatPenalty.MaxEnergy)
                    : DefeatPenalty.MaxEnergy;
            }
            catch (Exception)
            {
                return DefeatPenalty.MaxEnergy;
            }
        }

        private static void EnsureTable()
        {
            if (_tableReady) return;
            lock (_tableLock)
            {
                if (_tableReady) return;
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS CharacterEnergy (
                        CharacterId INTEGER PRIMARY KEY,
                        Energy      INTEGER NOT NULL);";
                command.ExecuteNonQuery();
                _tableReady = true;
            }
        }

        private static SqliteConnection Open()
        {
            var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            return connection;
        }
    }
}
