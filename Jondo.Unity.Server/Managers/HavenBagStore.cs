using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What the haven bag keeps from one session to another: the chosen theme, the furniture that has been
    /// placed and what is inside the chest.
    ///
    /// Three tables, all per character. Furniture is stored per theme, because each one has its own
    /// room and its own cells: switching theme and coming back has to give back the room just as it
    /// was left.
    ///
    /// The chest stores items just like CharacterItems, with their uid, their quantity and their effects, so
    /// that an item stored and taken out comes back identical. What goes into the chest is DELETED from the
    /// inventory and vice versa: an item is in one place or the other, never in both.
    ///
    /// Moving items in and out is <see cref="StorageStacks"/>'s, as for every other storage: a
    /// stack changes uid when it changes side, and the highest uid in the chest is kept out of the
    /// uid dispenser's way at start, so it is never handed out again after a restart.
    /// </summary>
    public static class HavenBagStore
    {
        public sealed class Furniture
        {
            public int Cell { get; init; }
            public long TypeId { get; init; }
            public int Orientation { get; init; }
        }

        public sealed class StoredItem
        {
            public long Uid { get; init; }
            public int Gid { get; init; }
            public int Quantity { get; init; }
            public string Effects { get; init; } = "";
        }

        public static void Initialize()
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS HavenBag (
                        CharacterId INTEGER PRIMARY KEY,
                        ThemeId     INTEGER NOT NULL DEFAULT 1);

                    CREATE TABLE IF NOT EXISTS HavenBagFurniture (
                        CharacterId INTEGER NOT NULL,
                        ThemeId     INTEGER NOT NULL,
                        Cell        INTEGER NOT NULL,
                        TypeId      INTEGER NOT NULL,
                        Orientation INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY (CharacterId, ThemeId, Cell));

                    CREATE TABLE IF NOT EXISTS HavenBagChest (
                        Uid         INTEGER PRIMARY KEY,
                        CharacterId INTEGER NOT NULL,
                        Gid         INTEGER NOT NULL,
                        Quantity    INTEGER NOT NULL DEFAULT 1,
                        Effects     TEXT);";
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Merkasako] No se pudieron crear las tablas: {ex.Message}");
            }

            // The chest's uids above the dispenser, as the bank's: see StorageStacks.EnsureTables.
            StorageStacks.EnsureTables();
        }

        // ─── The theme ──────────────────────────────────────────────────────────

        public static int ThemeOf(long characterId)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT ThemeId FROM HavenBag WHERE CharacterId = $id;";
                command.Parameters.AddWithValue("$id", characterId);
                if (command.ExecuteScalar() is long theme) return (int)theme;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Merkasako] No se pudo leer el decorado: {ex.Message}");
            }
            return Merkasako.DefaultTheme;
        }

        public static void SaveTheme(long characterId, int theme)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO HavenBag (CharacterId, ThemeId) VALUES ($id, $t) " +
                                      "ON CONFLICT(CharacterId) DO UPDATE SET ThemeId = $t;";
                command.Parameters.AddWithValue("$id", characterId);
                command.Parameters.AddWithValue("$t", theme);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Merkasako] No se pudo guardar el decorado: {ex.Message}");
            }
        }

        // ─── The furniture ──────────────────────────────────────────────────────

        public static List<Furniture> FurnitureOf(long characterId, int theme)
        {
            var salida = new List<Furniture>();
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT Cell, TypeId, Orientation FROM HavenBagFurniture " +
                                      "WHERE CharacterId = $id AND ThemeId = $t ORDER BY Cell;";
                command.Parameters.AddWithValue("$id", characterId);
                command.Parameters.AddWithValue("$t", theme);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    salida.Add(new Furniture
                    {
                        Cell = reader.GetInt32(0),
                        TypeId = reader.GetInt64(1),
                        Orientation = reader.GetInt32(2),
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Merkasako] No se pudieron leer los muebles: {ex.Message}");
            }
            return salida;
        }

        /// <summary>
        /// Stores the whole room. The client ALWAYS sends the complete list on accepting, not
        /// the differences, so whatever that theme had is deleted and the new one written: if
        /// not, a removed piece of furniture would never go away.
        /// </summary>
        public static void SaveFurniture(long characterId, int theme, IEnumerable<Furniture> pieces)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var transaction = connection.BeginTransaction();

                var borrar = connection.CreateCommand();
                borrar.CommandText = "DELETE FROM HavenBagFurniture WHERE CharacterId = $id AND ThemeId = $t;";
                borrar.Parameters.AddWithValue("$id", characterId);
                borrar.Parameters.AddWithValue("$t", theme);
                borrar.ExecuteNonQuery();

                foreach (var piece in pieces)
                {
                    var insertar = connection.CreateCommand();
                    insertar.CommandText = "INSERT OR REPLACE INTO HavenBagFurniture " +
                                           "(CharacterId, ThemeId, Cell, TypeId, Orientation) " +
                                           "VALUES ($id, $t, $c, $f, $o);";
                    insertar.Parameters.AddWithValue("$id", characterId);
                    insertar.Parameters.AddWithValue("$t", theme);
                    insertar.Parameters.AddWithValue("$c", piece.Cell);
                    insertar.Parameters.AddWithValue("$f", piece.TypeId);
                    insertar.Parameters.AddWithValue("$o", piece.Orientation);
                    insertar.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Merkasako] No se pudieron guardar los muebles: {ex.Message}");
            }
        }

        // ─── The chest ──────────────────────────────────────────────────────────

        /// <summary>What is in this character's chest, oldest uid first.</summary>
        public static List<StoredItem> ChestOf(long characterId)
            => StorageStacks.ItemsOf(StorageStacks.HavenBag(characterId));

        /// <summary>An inventory item, read just like the chest's.</summary>
        public static StoredItem? FromInventory(long characterId, long uid)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT Gid, Quantity, Effects FROM CharacterItems " +
                                      "WHERE Uid = $uid AND CharacterId = $id;";
                command.Parameters.AddWithValue("$uid", uid);
                command.Parameters.AddWithValue("$id", characterId);

                using var reader = command.ExecuteReader();
                if (!reader.Read()) return null;

                return new StoredItem
                {
                    Uid = uid,
                    Gid = reader.GetInt32(0),
                    Quantity = reader.IsDBNull(1) ? 1 : reader.GetInt32(1),
                    Effects = reader.IsDBNull(2) ? "" : reader.GetString(2),
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Merkasako] No se pudo leer el objeto {uid}: {ex.Message}");
                return null;
            }
        }
    }
}
