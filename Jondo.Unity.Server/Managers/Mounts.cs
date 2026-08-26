using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
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
            _porTipo.Clear();
            _rideable.Clear();
            _tipos.Clear();

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
                LeerMascoturas();
                LeerColoresQueFaltaban();
                AprenderAspectosPorTipo();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Mounts] No se pudo leer {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        /// <summary>
        /// Los tipos de objeto que se montan.
        ///
        /// Aquí estaba el fallo por el que no se veía NADA al equiparse una montura. Decía
        /// { 121, 311 } y ninguno de los dos vale: en la base no hay un solo objeto de tipo 311, y
        /// esos dos números salían de leer mal docs/appearances.md, donde "121" y "311" son
        /// RECUENTOS de prendas medidas, no tipos de objeto.
        ///
        /// Los de verdad son seis, tres especies con su tipo viejo y su tipo nuevo:
        ///
        ///    97 y 331   dragopavo       196 y 332   mulagua       207 y 333   vueloceronte
        ///
        /// La Mulagua del usuario, la 33306, es del 332. Con el conjunto de antes, IsRideable le
        /// decía que no, Ridden() devolvía null y el personaje se dibujaba a pie.
        ///
        /// Las MASCOTURAS, tipo 121, también ocupan el hueco 8, pero de sus veinticinco objetos no
        /// hay ni uno con aspecto conocido, así que se quedan fuera a propósito: es mejor no
        /// montarlas que dibujar un esqueleto vacío.
        /// </summary>
        private static readonly HashSet<int> RideableTypes = new HashSet<int>
        {
            97, 196, 207, 331, 332, 333, Mascotura
        };

        /// <summary>El tipo de objeto de las mascoturas.</summary>
        public const int Mascotura = 121;

        /// <summary>
        /// El aspecto de las mascoturas, que no está en ningún bundle y hay que medirlo.
        ///
        /// extract_monturas.py sólo encuentra dragopavos, mulaguas y vuelocerontes: las mascoturas
        /// no salen ni en MountsDataRoot ni en RidesDataRoot. Los suyos salen de verlas puestas en
        /// la captura del servidor de torneos, y de eso se encarga tools/extraer_mascoturas.py.
        ///
        /// Va en su propio fichero y no dentro de mounts.json porque aquél lo regenera
        /// extract_monturas.py de los bundles y se llevaría esto por delante.
        /// </summary>
        private static void LeerMascoturas()
        {
            string path = Path.Combine(Path.GetDirectoryName(Paths.MountsJson) ?? "", "mascoturas.json");
            if (!File.Exists(path))
            {
                Console.WriteLine("[Mounts] No hay mascoturas.json; las mascoturas no se verán. " +
                                  "Genéralo con tools/extraer_mascoturas.py.");
                return;
            }

            int cuantas = 0;
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
                        Bones = entry.Value.TryGetProperty("b", out var b) ? b.GetInt32() : 0,
                        Scale = entry.Value.TryGetProperty("s", out var s) ? s.GetInt32() : 0,
                        Colors = colors,
                    };
                    cuantas++;
                }
                Console.WriteLine($"[Mounts] {cuantas} mascoturas con su aspecto, medidas de la captura.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Mounts] No se pudo leer mascoturas.json: {ex.Message}");
            }
        }

        /// <summary>
        /// Los colores de las monturas que no vienen en MountsDataRoot.
        ///
        /// De las ciento veinte mulaguas de tipo 332, mounts.json sólo trae sesenta y seis: los
        /// cuatro colores nuevos —ámbar, coral, azur y aguamarina— no tienen `look` en ningún sitio
        /// de esa tabla, y con ellos se caen sus cuatro mulaguas sueltas y las cincuenta parejas en
        /// las que participan. La 33306, "Mulagua aguamarina y turquesa", es una de ellas: salía con
        /// huesos y escala buenos pero sin colores, y el cliente pintaba entonces su paleta por
        /// defecto, que tira a salmón.
        ///
        /// Los que faltaban se han recuperado de otras dos fuentes del propio cliente, no de la
        /// imaginación: los PNJ decorativos "Muldo &lt;color&gt;" de NpcsDataRoot, que llevan el
        /// aspecto entero y cuyos once colores viejos cuadran exactamente con los de mounts.json; y
        /// el icono del objeto, que dice cuál de los dos colores de una pareja va a los huecos 1 y 3
        /// —el que cubre más píxeles— con ciento diez aciertos de ciento diez sobre las parejas que
        /// sí se conocen. Lo hace tools/extraer_colores_monturas.py, que lo mide en cada pasada.
        ///
        /// Va en su propio fichero, como mascoturas.json, porque mounts.json lo regenera
        /// extract_monturas.py de los bundles y se llevaría esto por delante. Y NO pisa lo que ya
        /// venía de allí: sólo rellena los huecos.
        /// </summary>
        private static void LeerColoresQueFaltaban()
        {
            string path = Path.Combine(Path.GetDirectoryName(Paths.MountsJson) ?? "",
                                       "monturas_colores.json");
            if (!File.Exists(path))
            {
                Console.WriteLine("[Mounts] No hay monturas_colores.json; las mulaguas de los " +
                                  "colores nuevos saldrán con la paleta por defecto del cliente. " +
                                  "Genéralo con tools/extraer_colores_monturas.py.");
                return;
            }

            int cuantas = 0, yaEstaban = 0;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out int itemGid)) continue;

                    // lo de mounts.json manda; esto sólo rellena
                    if (_byItem.ContainsKey(itemGid)) { yaEstaban++; continue; }

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
                        Bones = entry.Value.TryGetProperty("b", out var b) ? b.GetInt32() : 0,
                        Scale = entry.Value.TryGetProperty("s", out var s) ? s.GetInt32() : 0,
                        Colors = colors,
                    };
                    cuantas++;
                }
                Console.WriteLine($"[Mounts] {cuantas} monturas más con sus colores recuperados" +
                                  (yaEstaban > 0 ? $" ({yaEstaban} ya venían en mounts.json)." : "."));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Mounts] No se pudo leer monturas_colores.json: {ex.Message}");
            }
        }

        /// <summary>
        /// El aspecto de reserva de cada TIPO, sacado de los objetos de ese tipo que sí lo tienen.
        ///
        /// mounts.json no está completo: de las 120 mulaguas nuevas sólo trae 66, y la 33306 no es
        /// una de ellas. Pero el aspecto no varía dentro de una especie —los 71 objetos de tipo 196
        /// y los 66 del 332 llevan TODOS huesos 3588 y escala 115, los del 207 y el 333 llevan 5023
        /// y 85, y los del 97 y el 331, 639 y 120—, así que para los que faltan se toma el de sus
        /// hermanos. No va escrito a mano: se cuenta al arrancar, sobre el propio fichero.
        ///
        /// Sin colores, que ésos sí son de cada montura. Con monturas_colores.json esas 54 ya no
        /// llegan aquí —vienen con huesos, escala y colores—, así que esto queda de red por si
        /// apareciera algún objeto de montura nuevo; y sigue sin inventarse colores, que es lo que
        /// hace el propio cliente cuando la raíz no los trae.
        /// </summary>
        private static readonly Dictionary<int, Look> _porTipo = new Dictionary<int, Look>();

        private static void AprenderAspectosPorTipo()
        {
            var cuentas = new Dictionary<int, Dictionary<(int, int), int>>();
            try
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                    DatabaseManager.WorldConnectionString);
                connection.Open();

                foreach (var (gid, look) in _byItem)
                {
                    if (look.Bones == 0) continue;
                    var command = connection.CreateCommand();
                    command.CommandText = "SELECT Type FROM ItemTemplates WHERE Id = $id;";
                    command.Parameters.AddWithValue("$id", gid);
                    object? valor = command.ExecuteScalar();
                    if (valor == null || valor == DBNull.Value) continue;

                    int tipo = Convert.ToInt32(valor);

                    // Las mascoturas NO entran en esto. En una especie de montura el esqueleto es
                    // el mismo para las ciento veinte mulaguas, así que a la que falte se le puede
                    // poner el de sus hermanas; pero cada mascotura es un bicho distinto —un
                    // kolifante no se parece a un murciélago— y ponerle el esqueleto de otra sería
                    // dibujar un animal que no es. Las tres que no están medidas se quedan sin
                    // montar, que es lo honrado.
                    if (tipo == Mascotura) continue;
                    if (!cuentas.TryGetValue(tipo, out var deEsteTipo))
                    {
                        deEsteTipo = new Dictionary<(int, int), int>();
                        cuentas[tipo] = deEsteTipo;
                    }
                    var forma = (look.Bones, look.Scale);
                    deEsteTipo.TryGetValue(forma, out int veces);
                    deEsteTipo[forma] = veces + 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Mounts] No se pudieron agrupar los aspectos por tipo: {ex.Message}");
                return;
            }

            foreach (var (tipo, formas) in cuentas)
            {
                int mejor = 0;
                (int Bones, int Scale) elegida = (0, 0);
                foreach (var (forma, veces) in formas)
                {
                    if (veces > mejor) { mejor = veces; elegida = forma; }
                }
                if (elegida.Bones == 0) continue;
                _porTipo[tipo] = new Look { Bones = elegida.Bones, Scale = elegida.Scale };
            }

            if (_porTipo.Count > 0)
            {
                Console.WriteLine("[Mounts] Aspecto de reserva por tipo: " +
                    string.Join(", ", _porTipo.Select(p => $"{p.Key}→{p.Value.Bones}/{p.Value.Scale}")));
            }
        }

        private static readonly Dictionary<int, bool> _rideable = new Dictionary<int, bool>();

        public static Look? Of(int itemGid)
        {
            if (_byItem.TryGetValue(itemGid, out var look)) return look;
            if (!IsRideable(itemGid)) return null;

            // Se monta pero no está en el fichero: se le pone el aspecto de los de su tipo. Antes
            // aquí se devolvía un Look vacío, y como BreedLookTable exige huesos distintos de cero
            // para dar a alguien por montado, daba igual reconocerla: seguía saliendo a pie.
            int tipo = TypeOf(itemGid);
            return tipo != 0 && _porTipo.TryGetValue(tipo, out var deSuTipo) ? deSuTipo : null;
        }

        /// <summary>El tipo de un objeto, cacheado.</summary>
        private static readonly Dictionary<int, int> _tipos = new Dictionary<int, int>();

        private static int TypeOf(int itemGid)
        {
            if (_tipos.TryGetValue(itemGid, out int ya)) return ya;

            int tipo = 0;
            try
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                    DatabaseManager.WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT Type FROM ItemTemplates WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", itemGid);
                object? valor = command.ExecuteScalar();
                if (valor != null && valor != DBNull.Value) tipo = Convert.ToInt32(valor);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Mounts] No se pudo mirar el tipo de {itemGid}: {ex.Message}");
            }

            _tipos[itemGid] = tipo;
            return tipo;
        }

        /// <summary>¿Es un objeto de los que se montan, aunque no sepamos dibujarlo?</summary>
        public static bool IsRideable(int itemGid)
        {
            if (_rideable.TryGetValue(itemGid, out bool known)) return known;

            bool salida = RideableTypes.Contains(TypeOf(itemGid));
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
