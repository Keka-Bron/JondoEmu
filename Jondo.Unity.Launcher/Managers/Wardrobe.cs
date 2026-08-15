using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// Lo que el personaje lleva de adorno: el título, el ornamento y las prendas de apariencia.
    ///
    /// Son tres cosas distintas aunque el juego las enseñe en la misma ventana:
    ///
    ///   El TÍTULO es el texto que sale bajo el nombre. Uno a la vez, o ninguno.
    ///   El ORNAMENTO es el marco que rodea al nombre. Uno a la vez, o ninguno.
    ///   Las APARIENCIAS son prendas que tapan lo que llevas puesto de verdad: un sombrero
    ///   cosmético se dibuja en lugar del sombrero que da las características.
    ///
    /// Las tres se guardan por personaje y sobreviven a la sesión, que es lo que se pide. La
    /// apariencia se guarda por hueco, porque cada prenda tapa un hueco concreto y hay que poder
    /// quitarla sola.
    /// </summary>
    public static class Wardrobe
    {
        /// <summary>Ninguno. El cliente manda cero para quitarse el título o el ornamento.</summary>
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

                // El ojo de mostrar/ocultar de la ventana: se puede llevar una prenda puesta y que
                // no se dibuje. Va aparte de quitarla, porque al volver a enseñarla sigue ahí. Se
                // añade con un ALTER porque la tabla ya existía sin él en las instalaciones de
                // antes.
                try
                {
                    var añadir = connection.CreateCommand();
                    añadir.CommandText =
                        "ALTER TABLE CharacterAppearance ADD COLUMN Hidden INTEGER NOT NULL DEFAULT 0;";
                    añadir.ExecuteNonQuery();
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // ya estaba
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Apariencias] No se pudieron crear las tablas: {ex.Message}");
            }
        }

        // ─── Título y ornamento ─────────────────────────────────────────────────

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

        // ─── Las prendas de apariencia ──────────────────────────────────────────

        /// <summary><c>Hidden</c> es el ojo de la ventana: la prenda sigue puesta pero no se dibuja.</summary>
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

        /// <summary>Pone una prenda en su hueco, echando la que hubiera.</summary>
        public static void Wear(long characterId, int slot, long uid, int gid)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                // Al poner una prenda nueva el ojo vuelve a abrirse: lo que acabas de elegir se ve.
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
        /// El ojo de mostrar/ocultar. La prenda se queda puesta; solo deja de dibujarse.
        ///
        /// El cliente lo pide con <c>lxg { f1: hueco, f3: 1 }</c> para ocultar y con el f3 ausente
        /// para volver a enseñarla. Medido en la captura de jugar con mostrar/ocultar: al ocultar,
        /// la piel de ese hueco desaparece de la lista y al mostrar vuelve.
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

        /// <summary>Quita lo que hubiera en un hueco. Con hueco negativo, lo quita todo.</summary>
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
