using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// El aspecto de cada montura, y cómo se monta uno encima.
    ///
    /// Una montura equipada no añade nada al personaje: lo SUSTITUYE. El cuerpo que se dibuja pasa
    /// a ser el de la montura, y el jinete viaja dentro, como subentidad. Leído de una captura real
    /// de equipar un dragopavo sin ningún cosmético puesto:
    ///
    ///   lxc  f2 { f1: colores de la montura
    ///             f2: 3
    ///             f3: 639        ← los huesos del dragopavo
    ///             f5: [120]      ← su escala
    ///             f7 { f1: { ...el aspecto del jinete, con huesos 2... }, f4: 2 } }
    ///
    /// Dos detalles que no se ven a simple vista: el jinete cambia sus huesos de 1 a 2 —el cliente
    /// tiene una tabla RiderBones con cuatro entradas y el 2 es el normal— y la montura va al
    /// hueco 8, el mismo que las mascotas.
    ///
    /// Los datos salen de MountsDataRoot del cliente, con tools/extract_monturas.py.
    /// </summary>
    public static class Mounts
    {
        /// <summary>El hueco donde va una montura o una mascota.</summary>
        public const int Slot = 8;

        /// <summary>Los huesos del jinete cuando va montado. Sin montura son los de su raza.</summary>
        public const int RiderBones = 2;

        /// <summary>Dónde se engancha el jinete a la montura.</summary>
        public const int RiderBindingPoint = 2;

        public sealed class Look
        {
            public int MountId { get; init; }
            public int Bones { get; init; }
            public int Scale { get; init; }
            public IReadOnlyList<long> Colors { get; init; } = Array.Empty<long>();
        }

        private static readonly Dictionary<int, Look> _byItem = new Dictionary<int, Look>();

        public static int Count => _byItem.Count;

        public static void Initialize()
        {
            _byItem.Clear();

            string path = Paths.MountsJson;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Mounts] Falta {Path.GetFileName(path)}; nadie se subirá a nada. " +
                                  "Genéralo con tools/extract_monturas.py.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out int itemGid)) continue;

                    var colors = new List<long>();
                    if (entry.Value.TryGetProperty("c", out var list))
                    {
                        foreach (var color in list.EnumerateArray())
                        {
                            if (color.TryGetInt64(out long value)) colors.Add(value);
                        }
                    }

                    _byItem[itemGid] = new Look
                    {
                        MountId = entry.Value.TryGetProperty("m", out var m) ? m.GetInt32() : 0,
                        Bones = entry.Value.TryGetProperty("b", out var b) ? b.GetInt32() : 0,
                        Scale = entry.Value.TryGetProperty("s", out var s) ? s.GetInt32() : 0,
                        Colors = colors,
                    };
                }
                Console.WriteLine($"[Mounts] {_byItem.Count} objetos de montura con su aspecto.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Mounts] No se pudo leer {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        /// <summary>
        /// Tipos de objeto que se montan aunque no estén en mounts.json.
        ///
        /// Ese fichero lo genera extract_monturas.py de los bundles y solo trae dragopavos, mulaguas
        /// y vuelocerontes. Las MASCOTURAS —tipo 121, veinticinco en el cliente— también ocupan el
        /// hueco 8 y también se montan, pero su aspecto no está ahí. Se reconocen para que el
        /// personaje cuente como montado; el aspecto lo pone la prenda de apariencia del hueco 5.
        /// </summary>
        private static readonly HashSet<int> RideableTypes = new HashSet<int> { 121, 311 };

        /// <summary>Se monta, pero no sabemos con qué aspecto: los huesos van a cero.</summary>
        private static readonly Look Unknown = new Look();

        private static readonly Dictionary<int, bool> _rideable = new Dictionary<int, bool>();

        public static Look? Of(int itemGid)
        {
            if (_byItem.TryGetValue(itemGid, out var look)) return look;
            return IsRideable(itemGid) ? Unknown : null;
        }

        /// <summary>¿Es un objeto de los que se montan, aunque no sepamos dibujarlo?</summary>
        public static bool IsRideable(int itemGid)
        {
            if (_rideable.TryGetValue(itemGid, out bool known)) return known;

            bool salida = false;
            try
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                    DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT Type FROM ItemTemplates WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", itemGid);

                object? valor = command.ExecuteScalar();
                if (valor != null && valor != DBNull.Value)
                {
                    salida = RideableTypes.Contains(Convert.ToInt32(valor));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Mounts] No se pudo mirar el tipo de {itemGid}: {ex.Message}");
            }

            _rideable[itemGid] = salida;
            return salida;
        }

        /// <summary>
        /// La montura que el personaje lleva puesta ahora mismo, o null si va a pie.
        ///
        /// En el hueco 8 caben también las mascotas, así que no vale con mirar que haya algo: hay
        /// que comprobar que ese objeto sea de verdad una montura.
        /// </summary>
        public static Look? Ridden()
        {
            foreach (var item in Equipment.All)
            {
                if (item.Position != Slot) continue;
                var look = Of(item.Template);
                if (look != null) return look;
            }
            return null;
        }

        /// <summary>
        /// La montura de un personaje cualquiera, preguntándoselo a la base de datos.
        ///
        /// <see cref="Ridden"/> solo sabe del que está jugando, porque mira el inventario cargado en
        /// memoria. En la pantalla de selección no hay ninguno cargado todavía y hay que enseñar el
        /// aspecto de todos, así que ahí se pregunta por id.
        /// </summary>
        public static Look? RiddenBy(long characterId)
        {
            if (characterId == 0) return null;

            try
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                    DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT Gid FROM CharacterItems " +
                                      "WHERE CharacterId = $id AND Position = $slot;";
                command.Parameters.AddWithValue("$id", characterId);
                command.Parameters.AddWithValue("$slot", Slot);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var look = Of(reader.GetInt32(0));
                    if (look != null) return look;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Mounts] No se pudo mirar la montura de {characterId}: {ex.Message}");
            }
            return null;
        }
    }
}
