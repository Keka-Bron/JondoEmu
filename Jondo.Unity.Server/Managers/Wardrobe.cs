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

                // The background of the player card, picked in its own window (kye).
                try
                {
                    var fondo = connection.CreateCommand();
                    fondo.CommandText =
                        "ALTER TABLE CharacterWardrobe ADD COLUMN CardBackground INTEGER NOT NULL DEFAULT 0;";
                    fondo.ExecuteNonQuery();
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // it was already there
                }

                // The aura picked in the appearance window or the emotes (lxw); 0 is none.
                try
                {
                    var aura = connection.CreateCommand();
                    aura.CommandText =
                        "ALTER TABLE CharacterWardrobe ADD COLUMN AuraId INTEGER NOT NULL DEFAULT 0;";
                    aura.ExecuteNonQuery();
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // it was already there
                }

                // The idle picked in the appearance window's idles tab (lwu); 0 is none picked.
                try
                {
                    var espera = connection.CreateCommand();
                    espera.CommandText =
                        "ALTER TABLE CharacterWardrobe ADD COLUMN IdleId INTEGER NOT NULL DEFAULT 0;";
                    espera.ExecuteNonQuery();
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // it was already there
                }

                // The body picked in the appearance window's bodies tab (lyo); 0 is the breed's own.
                try
                {
                    var cuerpo = connection.CreateCommand();
                    cuerpo.CommandText =
                        "ALTER TABLE CharacterWardrobe ADD COLUMN BodyId INTEGER NOT NULL DEFAULT 0;";
                    cuerpo.ExecuteNonQuery();
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // it was already there
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Appearances] Could not create the tables: {ex.Message}");
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
                Console.WriteLine($"[Appearances] Could not read the adornment: {ex.Message}");
            }
            return (None, None);
        }

        /// <summary>The player card's background; 0 is none chosen, which the client draws as its default.</summary>
        public static int CardBackgroundOf(long characterId)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT CardBackground FROM CharacterWardrobe WHERE CharacterId = $id;";
                command.Parameters.AddWithValue("$id", characterId);
                return command.ExecuteScalar() is long background ? (int)background : None;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Appearances] Could not read the card's background: {ex.Message}");
                return None;
            }
        }

        /// <summary>The body the appearance window picked; 0 for the breed's own.</summary>
        public static int BodyOf(long characterId)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT BodyId FROM CharacterWardrobe WHERE CharacterId = $id;";
                command.Parameters.AddWithValue("$id", characterId);
                return command.ExecuteScalar() is long body ? (int)body : None;
            }
            catch (Exception)
            {
                // the column comes with Initialize; before it there is no body to read
                return None;
            }
        }

        public static void SaveBody(long characterId, int bodyId)
            => Save(characterId, "BodyId", bodyId);

        /// <summary>The idle the appearance window picked; 0 for none.</summary>
        public static int IdleOf(long characterId)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT IdleId FROM CharacterWardrobe WHERE CharacterId = $id;";
                command.Parameters.AddWithValue("$id", characterId);
                return command.ExecuteScalar() is long idle ? (int)idle : None;
            }
            catch (Exception)
            {
                return None;
            }
        }

        public static void SaveIdle(long characterId, int idleId)
            => Save(characterId, "IdleId", idleId);

        /// <summary>The aura worn, an aura emote's id; 0 for none.</summary>
        /// <summary>The aura shown from the attitudes, until the next step; the worn one otherwise.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, int> _shown = new();

        /// <summary>An aura played from the attitudes: drawn on the character until he moves.</summary>
        public static void Show(long characterId, int auraId) => _shown[characterId] = auraId;

        /// <summary>The aura shown taken off, on a step. True when there was one.</summary>
        public static bool StopShowing(long characterId) => _shown.TryRemove(characterId, out _);

        /// <summary>The aura his look carries: the one shown from the attitudes, else the one worn.</summary>
        public static int ShownBy(long characterId)
            => _shown.TryGetValue(characterId, out int shown) ? shown : AuraOf(characterId);

        public static int AuraOf(long characterId)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT AuraId FROM CharacterWardrobe WHERE CharacterId = $id;";
                command.Parameters.AddWithValue("$id", characterId);
                return command.ExecuteScalar() is long aura ? (int)aura : None;
            }
            catch (Exception)
            {
                // the column comes with Initialize; before it nobody wears an aura
                return None;
            }
        }

        public static void SaveAura(long characterId, int auraId)
            => Save(characterId, "AuraId", auraId);

        public static void SaveCardBackground(long characterId, int background)
            => Save(characterId, "CardBackground", background);

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
                Console.WriteLine($"[Appearances] Could not save {column}: {ex.Message}");
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
                Console.WriteLine($"[Appearances] Could not read the garments: {ex.Message}");
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
                Console.WriteLine($"[Appearances] Could not put on the garment: {ex.Message}");
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
                // An empty slot can be hidden too, to hide the real piece under it (a shield with no
                // cosmetic): it is kept as a row with no garment, and dropped when shown again.
                command.CommandText = "INSERT INTO CharacterAppearance (CharacterId, Slot, Uid, Gid, Hidden) " +
                                      "VALUES ($id, $slot, 0, 0, $h) " +
                                      "ON CONFLICT(CharacterId, Slot) DO UPDATE SET Hidden = $h; " +
                                      "DELETE FROM CharacterAppearance " +
                                      "WHERE CharacterId = $id AND Slot = $slot AND Gid = 0 AND Hidden = 0;";
                command.Parameters.AddWithValue("$id", characterId);
                command.Parameters.AddWithValue("$slot", slot);
                command.Parameters.AddWithValue("$h", hidden ? 1 : 0);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Appearances] Could not {(hidden ? "hide" : "show")} " +
                                  $"slot {slot}: {ex.Message}");
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
                Console.WriteLine($"[Appearances] Could not take off the garment: {ex.Message}");
            }
        }
    }
}
