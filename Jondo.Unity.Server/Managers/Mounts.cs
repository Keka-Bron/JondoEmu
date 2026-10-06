using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Each mount's look, and how one gets on top of it.
    ///
    /// An equipped mount adds nothing to the character: it REPLACES it. The body drawn becomes
    /// the mount's, and the rider travels inside, as a subentity. Read from a real capture
    /// of equipping a dragoturkey with no cosmetic on:
    ///
    ///   lxc  f2 { f1: the mount's colours
    ///             f2: 3
    ///             f3: 639        ← the dragoturkey's bones
    ///             f5: [120]      ← its scale
    ///             f7 { f1: { ...the rider's look, with bones 2... }, f4: 2 } }
    ///
    /// Two details not visible at first sight: the rider changes his bones from 1 to 2 —the client
    /// has a RiderBones table with four entries and 2 is the normal one— and the mount goes to
    /// slot 8, the same as pets.
    ///
    /// The data comes from the client's MountsDataRoot, with tools/extract_monturas.py.
    /// </summary>
    public static class Mounts
    {
        /// <summary>The slot where a mount or a pet goes.</summary>
        public const int Slot = 8;

        /// <summary>The rider's bones when mounted. Without a mount they are his breed's.</summary>
        public const int RiderBones = 2;

        /// <summary>Where the rider hooks onto the mount.</summary>
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

        /// <summary>Every mount's look, once each: a dragoturkey of each colour, a seemum, a rhineetle...</summary>
        public static IReadOnlyList<Look> AllLooks
            => _byItem.Values.GroupBy(l => (l.Bones, string.Join(",", l.Colors))).Select(g => g.First()).ToList();

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
        /// The item types that are ridden.
        ///
        /// Here was the bug for which NOTHING was seen on equipping a mount. It said
        /// { 121, 311 } and neither of them is right: in the base there is not a single item of type 311, and
        /// those two numbers came from misreading docs/appearances.md, where "121" and "311" are
        /// COUNTS of measured garments, not item types.
        ///
        /// The real ones are six, three species with their old type and their new type:
        ///
        ///    97 and 331   dragoturkey     196 and 332   mulagua       207 and 333   vueloceronte
        ///
        /// The user's Mulagua, 33306, is of 332. With the earlier set, IsRideable told
        /// it no, Ridden() returned null and the character was drawn on foot.
        ///
        /// The PETSMOUNTS, type 121, also take slot 8, but of their twenty-five items there is
        /// not one with a known look, so they are left out on purpose: it is better not to
        /// ride them than to draw an empty skeleton.
        /// </summary>
        private static readonly HashSet<int> RideableTypes = new HashSet<int>
        {
            97, 196, 207, 331, 332, 333, Mascotura
        };

        /// <summary>The petsmounts' item type.</summary>
        public const int Mascotura = 121;

        /// <summary>
        /// The petsmounts' look, which is in no bundle and has to be measured.
        ///
        /// extract_monturas.py only finds dragoturkeys, mulaguas and vuelocerontes: the petsmounts
        /// come out neither in MountsDataRoot nor in RidesDataRoot. Theirs come from seeing them worn in
        /// the tournament server's capture, and tools/extraer_mascoturas.py takes care of that.
        ///
        /// It goes in its own file and not inside mounts.json because that one is regenerated by
        /// extract_monturas.py from the bundles and would wipe this out.
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
        /// The colours of the mounts that do not come in MountsDataRoot.
        ///
        /// Of the hundred and twenty mulaguas of type 332, mounts.json only brings sixty-six: the
        /// four new colours —amber, coral, azure and aquamarine— have no `look` anywhere
        /// in that table, and with them fall their four single-colour mulaguas and the fifty pairs
        /// they take part in. 33306, "Mulagua aguamarina y turquesa", is one of them: it came out with
        /// good bones and scale but without colours, and the client then drew its default palette,
        /// which tends to salmon.
        ///
        /// The missing ones have been recovered from two other sources of the client itself, not from
        /// imagination: the decorative NPCs "Muldo &lt;color&gt;" of NpcsDataRoot, which carry the
        /// whole look and whose eleven old colours match those of mounts.json exactly; and
        /// the item's icon, which says which of a pair's two colours goes to slots 1 and 3
        /// —the one covering more pixels— with a hundred and ten hits out of a hundred and ten on the pairs that
        /// are known. tools/extraer_colores_monturas.py does it, and measures it on each pass.
        ///
        /// It goes in its own file, like mascoturas.json, because mounts.json is regenerated by
        /// extract_monturas.py from the bundles and would wipe this out. And it does NOT overwrite what already
        /// came from there: it only fills the gaps.
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

                    // mounts.json's rules; this only fills in
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
        /// Each TYPE's fallback look, taken from the items of that type that do have it.
        ///
        /// mounts.json is not complete: of the 120 new mulaguas it only brings 66, and 33306 is not
        /// one of them. But the look does not vary within a species —the 71 items of type 196
        /// and the 66 of 332 ALL carry bones 3588 and scale 115, those of 207 and 333 carry 5023
        /// and 85, and those of 97 and 331, 639 and 120—, so for the missing ones their
        /// siblings' is taken. It is not hand-written: it is counted at start, over the file itself.
        ///
        /// Without colours, which are indeed each mount's own. With monturas_colores.json those 54 no longer
        /// get here —they come with bones, scale and colours—, so this remains as a safety net in case
        /// some new mount item appeared; and it still does not invent colours, which is what
        /// the client itself does when the root does not bring them.
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

                    // Petsmounts do NOT go into this. In a mount species the skeleton is
                    // the same for all hundred and twenty mulaguas, so the one that is missing can be
                    // given its sisters'; but each petsmount is a different creature —a
                    // kolifante does not look like a bat— and giving it another's skeleton would be
                    // drawing an animal it is not. The three that are not measured stay unridden,
                    // which is the honest thing.
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

            // It is ridden but it is not in the file: it is given the look of those of its type. Before,
            // an empty Look was returned here, and since BreedLookTable requires non-zero bones
            // to consider someone mounted, recognising it made no difference: he still came out on foot.
            int tipo = TypeOf(itemGid);
            return tipo != 0 && _porTipo.TryGetValue(tipo, out var deSuTipo) ? deSuTipo : null;
        }

        /// <summary>An item's type, cached.</summary>
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

        /// <summary>Is it an item of the ridden kind, even if we do not know how to draw it?</summary>
        public static bool IsRideable(int itemGid)
        {
            if (_rideable.TryGetValue(itemGid, out bool known)) return known;

            bool salida = RideableTypes.Contains(TypeOf(itemGid));
            _rideable[itemGid] = salida;
            return salida;
        }

        /// <summary>
        /// The mount the character has on right now, or null if he is on foot.
        ///
        /// Pets also fit in slot 8, so looking for something there is not enough: one
        /// has to check that the item really is a mount.
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
        /// Any character's mount, asking the database.
        ///
        /// <see cref="Ridden"/> only knows about the one playing, because it looks at the inventory loaded in
        /// memory. On the selection screen none is loaded yet and everyone's look has to be
        /// shown, so there it is asked by id.
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
