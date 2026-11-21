using System;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The changes a potion leaves waiting for the character's next connection: a new name, a new
    /// sex. One at a time, as the client's own popup 43 says («…ya se está realizando un cambio de
    /// nombre, de sexo, de color, de cara o de clase.»).
    /// </summary>
    /// <remarks>
    /// The potions say so themselves, in the client's popups: «Podrás elegir el nombre para tu
    /// personaje la próxima vez que te conectes.» (41), «Tu personaje cambiará de sexo la próxima
    /// vez que te conectes.» (44). The sex changes as the character is selected; the name is asked
    /// for once in the world (<see cref="Handlers.RemodelHandler"/>).
    /// </remarks>
    public static class CharacterRemodel
    {
        public enum Change { None = 0, Name = 1, Sex = 2 }

        private static readonly object _gate = new object();
        private static volatile bool _tableReady;

        /// <summary>The change waiting for that character, if any.</summary>
        public static Change PendingOf(long characterId)
        {
            try
            {
                EnsureTable();
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Change FROM CharacterPendingChanges WHERE CharacterId = $c;";
                command.Parameters.AddWithValue("$c", characterId);
                return command.ExecuteScalar() is long change ? (Change)change : Change.None;
            }
            catch (Exception)
            {
                return Change.None;
            }
        }

        /// <summary>Leaves a change waiting; false when another one already is.</summary>
        public static bool Request(long characterId, Change change)
        {
            try
            {
                EnsureTable();
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT OR IGNORE INTO CharacterPendingChanges (CharacterId, Change) VALUES ($c, $k);";
                command.Parameters.AddWithValue("$c", characterId);
                command.Parameters.AddWithValue("$k", (int)change);
                return command.ExecuteNonQuery() > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Remodel] Could not leave a change waiting for {characterId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>The waiting change done.</summary>
        public static void Clear(long characterId)
        {
            try
            {
                EnsureTable();
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM CharacterPendingChanges WHERE CharacterId = $c;";
                command.Parameters.AddWithValue("$c", characterId);
                command.ExecuteNonQuery();
            }
            catch (Exception)
            {
                // nothing waiting
            }
        }

        /// <summary>
        /// The waiting sex change, done as the character is selected: the other sex, the head of
        /// the other sex at the same place among its breed's, and the breed's own body.
        /// </summary>
        public static bool ApplySexChange(long characterId)
        {
            if (PendingOf(characterId) != Change.Sex) return false;
            var character = DatabaseManager.GetCharacterById(characterId);
            if (character == null) return false;

            int sex = character.Sex == 1 ? 0 : 1;
            int head = HeadTable.Counterpart(character.HeadId, character.Breed, character.Sex);
            if (!DatabaseManager.SetCharacterSex(characterId, sex)) return false;
            if (head > 0) DatabaseManager.SetCharacterHead(characterId, head);
            Wardrobe.SaveBody(characterId, Wardrobe.None);
            Clear(characterId);
            Console.WriteLine($"[Remodel] {character.Name} changes sex ({character.Sex} → {sex}, head {head}).");
            return true;
        }

        private static void EnsureTable()
        {
            if (_tableReady) return;
            lock (_gate)
            {
                if (_tableReady) return;
                using var connection = Open();
                using var create = connection.CreateCommand();
                create.CommandText = "CREATE TABLE IF NOT EXISTS CharacterPendingChanges (" +
                                     "CharacterId INTEGER PRIMARY KEY, Change INTEGER NOT NULL);";
                create.ExecuteNonQuery();
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
