using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What the character wears as adornment: the title, the ornament and the appearance garments.
    ///
    /// They are three different things even though the game shows them in the same window:
    ///
    ///   The TITLE is the text shown under the name. One at a time, or none.
    ///   The ORNAMENT is the frame around the name. One at a time, or none.
    ///   The APPEARANCES are garments that cover what you really have on: a cosmetic
    ///   hat is drawn in place of the hat that gives the characteristics.
    ///
    /// All three are stored per character and survive the session, which is what is asked. The
    /// appearance is stored per slot, because each garment covers a specific slot and it has to be possible to
    /// take it off on its own.
    /// </summary>
    public static class Wardrobe
    {
        /// <summary>None. The client sends zero to take off the title or the ornament.</summary>
        public const int None = 0;

        public static void Initialize()
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS CharacterWardrobe (
                        CharacterId INTEGER PRIMARY KEY,
                        TitleId     INTEGER NOT NULL DEFAULT 0,
                        OrnamentId  INTEGER NOT NULL DEFAULT 0);

                    CREATE TABLE IF NOT EXISTS CharacterAppearance (
                        CharacterId INTEGER NOT NULL,
                        Slot        INTEGER NOT NULL,
                        Uid         INTEGER NOT NULL,
                        Gid         INTEGER NOT NULL,
                        PRIMARY KEY (CharacterId, Slot));";
                command.ExecuteNonQuery();

                // The window's show/hide eye: a garment can be worn and
                // not drawn. It is separate from taking it off, because on showing it again it is still there. It is
                // added with an ALTER because the table already existed without it in the earlier
                // installations.
                try
                {
                    var añadir = connection.CreateCommand();
                    añadir.CommandText =
                        "ALTER TABLE CharacterAppearance ADD COLUMN Hidden INTEGER NOT NULL DEFAULT 0;";
                    añadir.ExecuteNonQuery();
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // it was already there
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Apariencias] No se pudieron crear las tablas: {ex.Message}");
            }
        }

        // ─── Title and ornament ─────────────────────────────────────────────────

        public static (int Title, int Ornament) Of(long characterId)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT TitleId, OrnamentId FROM CharacterWardrobe " +
                                      "WHERE CharacterId = $id;";
                command.Parameters.AddWithValue("$id", characterId);

                using var reader = command.ExecuteReader();
                if (reader.Read()) return (reader.GetInt32(0), reader.GetInt32(1));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Apariencias] No se pudo leer el adorno: {ex.Message}");
            }
            return (None, None);
        }

        public static void SaveTitle(long characterId, int titleId)
            => Save(characterId, "TitleId", titleId);

        public static void SaveOrnament(long characterId, int ornamentId)
            => Save(characterId, "OrnamentId", ornamentId);

        private static void Save(long characterId, string column, int value)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = $"INSERT INTO CharacterWardrobe (CharacterId, {column}) " +
                                      $"VALUES ($id, $v) " +
                                      $"ON CONFLICT(CharacterId) DO UPDATE SET {column} = $v;";
                command.Parameters.AddWithValue("$id", characterId);
                command.Parameters.AddWithValue("$v", value);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Apariencias] No se pudo guardar {column}: {ex.Message}");
            }
        }

        // ─── The appearance garments ────────────────────────────────────────────

        /// <summary><c>Hidden</c> is the window's eye: the garment is still on but is not drawn.</summary>
        public readonly record struct Worn(int Slot, long Uid, int Gid, bool Hidden);

        public static List<Worn> AppearanceOf(long characterId)
        {
            var salida = new List<Worn>();
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT Slot, Uid, Gid, Hidden FROM CharacterAppearance " +
                                      "WHERE CharacterId = $id ORDER BY Slot;";
                command.Parameters.AddWithValue("$id", characterId);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    salida.Add(new Worn(reader.GetInt32(0), reader.GetInt64(1), reader.GetInt32(2),
                                        reader.GetInt32(3) != 0));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Apariencias] No se pudieron leer las prendas: {ex.Message}");
            }
            return salida;
        }

        /// <summary>Puts a garment in its slot, throwing out whatever was there.</summary>
        public static void Wear(long characterId, int slot, long uid, int gid)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                // On putting on a new garment the eye opens again: what you have just chosen is seen.
                command.CommandText = "INSERT INTO CharacterAppearance (CharacterId, Slot, Uid, Gid, Hidden) " +
                                      "VALUES ($id, $slot, $uid, $gid, 0) " +
                                      "ON CONFLICT(CharacterId, Slot) DO UPDATE SET " +
                                      "Uid = $uid, Gid = $gid, Hidden = 0;";
                command.Parameters.AddWithValue("$id", characterId);
                command.Parameters.AddWithValue("$slot", slot);
                command.Parameters.AddWithValue("$uid", uid);
                command.Parameters.AddWithValue("$gid", gid);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Apariencias] No se pudo poner la prenda: {ex.Message}");
            }
        }

        /// <summary>
        /// The show/hide eye. The garment stays on; it only stops being drawn.
        ///
        /// The client asks for it with <c>lxg { f1: slot, f3: 1 }</c> to hide and with f3 absent
        /// to show it again. Measured in the capture of playing with show/hide: on hiding,
        /// that slot's skin disappears from the list and on showing it comes back.
        /// </summary>
        public static void SetHidden(long characterId, int slot, bool hidden)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "UPDATE CharacterAppearance SET Hidden = $h " +
                                      "WHERE CharacterId = $id AND Slot = $slot;";
                command.Parameters.AddWithValue("$id", characterId);
                command.Parameters.AddWithValue("$slot", slot);
                command.Parameters.AddWithValue("$h", hidden ? 1 : 0);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Apariencias] No se pudo {(hidden ? "ocultar" : "enseñar")} " +
                                  $"el hueco {slot}: {ex.Message}");
            }
        }

        /// <summary>Removes whatever was in a slot. With a negative slot, it removes everything.</summary>
        public static void TakeOff(long characterId, int slot)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = slot < 0
                    ? "DELETE FROM CharacterAppearance WHERE CharacterId = $id;"
                    : "DELETE FROM CharacterAppearance WHERE CharacterId = $id AND Slot = $slot;";
                command.Parameters.AddWithValue("$id", characterId);
                if (slot >= 0) command.Parameters.AddWithValue("$slot", slot);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Apariencias] No se pudo quitar la prenda: {ex.Message}");
            }
        }
    }
}
