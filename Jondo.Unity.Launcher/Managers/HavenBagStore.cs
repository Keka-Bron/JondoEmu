using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// Lo que el merkasako guarda de una sesión a otra: el decorado elegido, los muebles que se han
    /// colocado y lo que hay dentro del cofre.
    ///
    /// Tres tablas, todas por personaje. Los muebles se guardan por decorado, porque cada uno tiene
    /// su propia habitación y sus propias casillas: cambiarse de tema y volver tiene que devolver la
    /// habitación tal como se dejó.
    ///
    /// El cofre guarda objetos igual que CharacterItems, con su uid, su cantidad y sus efectos, para
    /// que un objeto guardado y sacado vuelva idéntico. Lo que se mete en el cofre se BORRA del
    /// inventario y al revés: un objeto está en un sitio o en el otro, nunca en los dos.
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
        }

        // ─── El decorado ────────────────────────────────────────────────────────

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

        // ─── Los muebles ────────────────────────────────────────────────────────

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
        /// Guarda la habitación entera. El cliente manda SIEMPRE la lista completa al aceptar, no
        /// las diferencias, así que se borra lo que había de ese decorado y se escribe lo nuevo: si
        /// no, un mueble quitado no se iba nunca.
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

        // ─── El cofre ───────────────────────────────────────────────────────────

        public static List<StoredItem> ChestOf(long characterId)
        {
            var salida = new List<StoredItem>();
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT Uid, Gid, Quantity, Effects FROM HavenBagChest " +
                                      "WHERE CharacterId = $id;";
                command.Parameters.AddWithValue("$id", characterId);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    salida.Add(new StoredItem
                    {
                        Uid = reader.GetInt64(0),
                        Gid = reader.GetInt32(1),
                        Quantity = reader.IsDBNull(2) ? 1 : reader.GetInt32(2),
                        Effects = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Merkasako] No se pudo leer el cofre: {ex.Message}");
            }
            return salida;
        }

        /// <summary>Del inventario al cofre. El objeto deja de estar en CharacterItems.</summary>
        public static bool PutIn(long characterId, long uid, int quantity)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var leer = connection.CreateCommand();
                leer.CommandText = "SELECT Gid, Quantity, Effects FROM CharacterItems " +
                                   "WHERE Uid = $uid AND CharacterId = $id;";
                leer.Parameters.AddWithValue("$uid", uid);
                leer.Parameters.AddWithValue("$id", characterId);

                int gid, tiene;
                string efectos;
                using (var reader = leer.ExecuteReader())
                {
                    if (!reader.Read()) return false;
                    gid = reader.GetInt32(0);
                    tiene = reader.IsDBNull(1) ? 1 : reader.GetInt32(1);
                    efectos = reader.IsDBNull(2) ? "" : reader.GetString(2);
                }

                int mueve = quantity <= 0 || quantity > tiene ? tiene : quantity;

                using var transaction = connection.BeginTransaction();

                if (mueve >= tiene)
                {
                    var quitar = connection.CreateCommand();
                    quitar.CommandText = "DELETE FROM CharacterItems WHERE Uid = $uid;";
                    quitar.Parameters.AddWithValue("$uid", uid);
                    quitar.ExecuteNonQuery();
                }
                else
                {
                    var restar = connection.CreateCommand();
                    restar.CommandText = "UPDATE CharacterItems SET Quantity = Quantity - $n WHERE Uid = $uid;";
                    restar.Parameters.AddWithValue("$n", mueve);
                    restar.Parameters.AddWithValue("$uid", uid);
                    restar.ExecuteNonQuery();
                }

                var meter = connection.CreateCommand();
                meter.CommandText = "INSERT INTO HavenBagChest (Uid, CharacterId, Gid, Quantity, Effects) " +
                                    "VALUES ($uid, $id, $gid, $n, $e) " +
                                    "ON CONFLICT(Uid) DO UPDATE SET Quantity = Quantity + $n;";
                meter.Parameters.AddWithValue("$uid", uid);
                meter.Parameters.AddWithValue("$id", characterId);
                meter.Parameters.AddWithValue("$gid", gid);
                meter.Parameters.AddWithValue("$n", mueve);
                meter.Parameters.AddWithValue("$e", efectos);
                meter.ExecuteNonQuery();

                transaction.Commit();

                Equipment.Remove(uid, mueve);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Merkasako] No se pudo meter {uid} en el cofre: {ex.Message}");
                return false;
            }
        }

        /// <summary>Del cofre al inventario, a la bolsa.</summary>
        public static bool TakeOut(long characterId, long uid, int quantity)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var leer = connection.CreateCommand();
                leer.CommandText = "SELECT Gid, Quantity, Effects FROM HavenBagChest " +
                                   "WHERE Uid = $uid AND CharacterId = $id;";
                leer.Parameters.AddWithValue("$uid", uid);
                leer.Parameters.AddWithValue("$id", characterId);

                int gid, tiene;
                string efectos;
                using (var reader = leer.ExecuteReader())
                {
                    if (!reader.Read()) return false;
                    gid = reader.GetInt32(0);
                    tiene = reader.IsDBNull(1) ? 1 : reader.GetInt32(1);
                    efectos = reader.IsDBNull(2) ? "" : reader.GetString(2);
                }

                int mueve = quantity <= 0 || quantity > tiene ? tiene : quantity;

                using var transaction = connection.BeginTransaction();

                if (mueve >= tiene)
                {
                    var quitar = connection.CreateCommand();
                    quitar.CommandText = "DELETE FROM HavenBagChest WHERE Uid = $uid;";
                    quitar.Parameters.AddWithValue("$uid", uid);
                    quitar.ExecuteNonQuery();
                }
                else
                {
                    var restar = connection.CreateCommand();
                    restar.CommandText = "UPDATE HavenBagChest SET Quantity = Quantity - $n WHERE Uid = $uid;";
                    restar.Parameters.AddWithValue("$n", mueve);
                    restar.Parameters.AddWithValue("$uid", uid);
                    restar.ExecuteNonQuery();
                }

                var devolver = connection.CreateCommand();
                devolver.CommandText = "INSERT INTO CharacterItems (Uid, CharacterId, Gid, Quantity, Position, Effects) " +
                                       "VALUES ($uid, $id, $gid, $n, $bolsa, $e) " +
                                       "ON CONFLICT(Uid) DO UPDATE SET Quantity = Quantity + $n;";
                devolver.Parameters.AddWithValue("$uid", uid);
                devolver.Parameters.AddWithValue("$id", characterId);
                devolver.Parameters.AddWithValue("$gid", gid);
                devolver.Parameters.AddWithValue("$n", mueve);
                devolver.Parameters.AddWithValue("$bolsa", Equipment.Bag);
                devolver.Parameters.AddWithValue("$e", efectos);
                devolver.ExecuteNonQuery();

                transaction.Commit();

                Equipment.Add(uid, gid, mueve, Equipment.Bag, efectos);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Merkasako] No se pudo sacar {uid} del cofre: {ex.Message}");
                return false;
            }
        }

        /// <summary>Un objeto del inventario, leído igual que los del cofre.</summary>
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

        public static bool Holds(long characterId, long uid)
        {
            foreach (var item in ChestOf(characterId))
            {
                if (item.Uid == uid) return true;
            }
            return false;
        }
    }
}
