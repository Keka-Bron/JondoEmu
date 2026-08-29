using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Las prendas de apariencia: qué existe y qué aspecto tiene cada una.
    ///
    /// Son dos ficheros y conviene no confundirlos:
    ///
    ///   cosmetics.json       el CATÁLOGO, sacado del cliente: 2.409 objetos repartidos en los 12
    ///                        tipos que el cliente marca como categoría 5 (sombreros, capas,
    ///                        escudos, trajes, alas, hombreras, mascotas, mascoturas y demás).
    ///   cosmetic_skins.json  el ASPECTO de cada una, que NO está en el cliente y se ha medido
    ///                        comparando el bloque de aspecto antes y después de equipar en las
    ///                        capturas reales.
    ///
    /// De las capturas se sabe además cómo cambia cada tipo de prenda el aspecto, y no todas lo
    /// hacen igual:
    ///
    ///   una capa, un sombrero, un escudo, un traje, unas alas o unas hombreras METEN UN NÚMERO en
    ///   la lista de pieles (el f6) — en el juego real sustituyen al de la prenda de verdad, aquí
    ///   se añade porque nunca hemos sabido el de la prenda de verdad;
    ///   una mascotura CAMBIA LOS HUESOS de la raíz, y no toca las pieles;
    ///   una mascota CUELGA una subentidad del enganche 1;
    ///   un aura, otra del enganche 6.
    /// </summary>
    public static class Cosmetics
    {
        /// <summary>Los huecos de la ventana de apariencia, de las capturas.</summary>
        public const int SlotAmulet = 0;
        public const int SlotMount = 5;
        public const int SlotCape = 9;
        public const int SlotHat = 10;
        public const int SlotPet = 11;
        public const int SlotShield = 12;
        public const int SlotCostume = 23;
        public const int SlotWings = 24;
        public const int SlotShoulders = 25;

        /// <summary>
        /// El hueco al que va cada tipo de objeto. Sirve de red: cuando el hueco está MEDIDO en las
        /// capturas manda el medido, porque para dos familias esta tabla no puede acertar. Las 194
        /// armas de apariencia son todas del mismo tipo y se reparten en diez huecos (uno por tipo
        /// de arma real imitada), y un objeto viviente cambia de hueco según la variante que se le
        /// elija.
        /// </summary>
        private static readonly Dictionary<int, int> SlotOfType = new Dictionary<int, int>
        {
            { 246, SlotHat },        // sombrero de apariencia
            { 247, SlotCape },       // capa
            { 248, SlotShield },     // escudo
            { 249, SlotPet },        // mascota
            { 250, SlotMount },      // mascotura
            { 324, SlotMount },      // montura de apariencia
            { 199, SlotCostume },    // traje
            { 299, SlotShoulders },  // hombreras
            { 300, SlotWings },      // alas
            { 113, SlotAmulet },     // objeto viviente
            { 252, SlotAmulet },     // objeto diverso
            { 251, 13 },             // arma
        };

        public sealed class Piece
        {
            public int Type { get; init; }
            public int Level { get; init; }
        }

        /// <summary>
        /// El aspecto que impone una prenda que no va por pieles: una mascota, que cuelga del
        /// enganche 1, o una mascotura/montura, que manda en la raíz.
        ///
        /// La ESCALA ausente es la de por defecto, no cero: viaja como repetido empaquetado y un
        /// cero se codificaría explícitamente, así que cero aquí significa "no la toques".
        /// </summary>
        public sealed class PieceLook
        {
            public int Bones { get; init; }
            public int Scale { get; init; }
            /// <summary>La piel de la raíz; solo la ponen las monturas de apariencia.</summary>
            public int Skin { get; init; }
            /// <summary>Los colores medidos, ya empaquetados. Vacío si la prenda no los toca.</summary>
            public byte[]? Colors { get; init; }
            /// <summary>El color es el del PERSONAJE que la lleva, copiado byte a byte.</summary>
            public bool ColorsFromWearer { get; init; }
        }

        private static readonly Dictionary<int, Piece> _catalogue = new Dictionary<int, Piece>();
        // Casi todas las prendas meten UNA piel, pero hay tres medidas que meten dos —la capa
        // 18579, el escudo 13240 y el traje 18525—, así que el valor es una lista.
        private static readonly Dictionary<int, int[]> _skins = new Dictionary<int, int[]>();
        private static readonly Dictionary<int, Dictionary<int, int[]>> _variants
            = new Dictionary<int, Dictionary<int, int[]>>();
        private static readonly int[] _ninguna = Array.Empty<int>();
        private static readonly Dictionary<int, PieceLook> _mounts = new Dictionary<int, PieceLook>();
        private static readonly Dictionary<int, PieceLook> _pets = new Dictionary<int, PieceLook>();
        private static readonly Dictionary<int, int> _auras = new Dictionary<int, int>();
        // Huecos medidos: los de las armas van por objeto, los de los objetos vivientes por
        // (objeto, variante), porque una misma sortija imita una capa o un sombrero según cuál se
        // elija.
        private static readonly Dictionary<int, int> _slots = new Dictionary<int, int>();
        private static readonly Dictionary<int, Dictionary<int, int>> _slotsByVariant
            = new Dictionary<int, Dictionary<int, int>>();
        private static readonly List<int> _titles = new List<int>();
        private static readonly List<int> _ornaments = new List<int>();
        private static readonly Dictionary<int, int> _appearanceBones = new Dictionary<int, int>();

        public static int Count => _catalogue.Count;
        public static int KnownLooks => _skins.Count + _mounts.Count + _pets.Count + _variants.Count;
        public static IEnumerable<KeyValuePair<int, Piece>> All => _catalogue;
        /// <summary>Los títulos y ornamentos que el servidor real aceptó en las capturas.</summary>
        public static IReadOnlyList<int> MeasuredTitles => _titles;
        public static IReadOnlyList<int> MeasuredOrnaments => _ornaments;

        public static void Initialize()
        {
            _catalogue.Clear();
            _skins.Clear();
            _variants.Clear();
            _mounts.Clear();
            _pets.Clear();
            _auras.Clear();
            _slots.Clear();
            _slotsByVariant.Clear();
            _titles.Clear();
            _ornaments.Clear();
            _appearanceBones.Clear();

            LoadCatalogue();
            LoadLooks();

            int resueltas = 0;
            foreach (var gid in _catalogue.Keys)
            {
                if (_skins.ContainsKey(gid) || _variants.ContainsKey(gid) || _pets.ContainsKey(gid)
                    || _mounts.ContainsKey(gid) || _slots.ContainsKey(gid)
                    || _slotsByVariant.ContainsKey(gid)) resueltas++;
            }

            Console.WriteLine($"[Apariencias] {_catalogue.Count} prendas en el catálogo, " +
                              $"{resueltas} medidas ({100 * resueltas / Math.Max(1, _catalogue.Count)}%), " +
                              $"{_auras.Count} auras.");

            CheckMeasuredAgainstOffered();
        }

        /// <summary>
        /// Los títulos y ornamentos que el servidor real aceptó tienen que estar entre los que se
        /// ofrecen. Si algún día se regenera titles_ornaments.json y se pierde alguno, esto lo dice
        /// en vez de dejar un título que existe pero no se puede poner.
        /// </summary>
        private static void CheckMeasuredAgainstOffered()
        {
            if (_titles.Count == 0 && _ornaments.Count == 0) return;

            int faltanTítulos = 0, faltanOrnamentos = 0;
            var ofrecidos = new HashSet<long>(Titles.All);
            var ofrecidosOrn = new HashSet<long>(Titles.AllOrnaments);
            foreach (int id in _titles) if (!ofrecidos.Contains(id)) faltanTítulos++;
            foreach (int id in _ornaments) if (!ofrecidosOrn.Contains(id)) faltanOrnamentos++;

            if (faltanTítulos > 0 || faltanOrnamentos > 0)
            {
                Console.WriteLine($"[Apariencias][AVISO] {faltanTítulos} títulos y {faltanOrnamentos} " +
                                  $"ornamentos medidos en las capturas no están entre los que se ofrecen.");
            }
        }

        private static void LoadCatalogue()
        {
            string path = Paths.CosmeticsJson;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Apariencias] Falta {Path.GetFileName(path)}.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("items", out var items)) return;

                foreach (var entry in items.EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out int gid)) continue;
                    _catalogue[gid] = new Piece
                    {
                        Type = entry.Value.TryGetProperty("t", out var t) ? t.GetInt32() : 0,
                        Level = entry.Value.TryGetProperty("l", out var l) ? l.GetInt32() : 1,
                    };
                }

                // El efecto 335 lleva un identificador de apariencia, no los huesos directamente.
                // Las de tipo 5 son sustituciones simples del esqueleto: en 3.6.10.10 la forma
                // bestial del Ouginak es la apariencia 1260, que apunta a los huesos 9025.
                if (doc.RootElement.TryGetProperty("appearances", out var appearances))
                {
                    foreach (var entry in appearances.EnumerateObject())
                    {
                        if (!int.TryParse(entry.Name, out int id)) continue;
                        if (!entry.Value.TryGetProperty("t", out var t) || t.GetInt32() != 5) continue;
                        if (!entry.Value.TryGetProperty("d", out var d)) continue;

                        string raw = d.ValueKind == JsonValueKind.String
                            ? d.GetString() ?? ""
                            : d.GetRawText();
                        if (int.TryParse(raw, out int bones) && bones > 0)
                            _appearanceBones[id] = bones;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Apariencias] No se pudo leer el catálogo: {ex.Message}");
            }
        }

        private static void LoadLooks()
        {
            string path = Paths.CosmeticSkinsJson;
            if (!File.Exists(path)) return;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;

                ReadSkins(root, "skins", _skins);
                ReadPairs(root, "auras", _auras);
                ReadPairs(root, "slots", _slots);
                ReadLooks(root, "pets", _pets);
                ReadLooks(root, "mounts", _mounts);
                ReadIds(root, "titles", _titles);
                ReadIds(root, "ornaments", _ornaments);

                if (root.TryGetProperty("variants", out var variants))
                {
                    foreach (var entry in variants.EnumerateObject())
                    {
                        if (!int.TryParse(entry.Name, out int gid)) continue;
                        var tabla = new Dictionary<int, int[]>();
                        foreach (var v in entry.Value.EnumerateObject())
                        {
                            if (int.TryParse(v.Name, out int índice)) tabla[índice] = ReadSkinValue(v.Value);
                        }
                        _variants[gid] = tabla;
                    }
                }

                if (root.TryGetProperty("slotsVariante", out var porVariante))
                {
                    foreach (var entry in porVariante.EnumerateObject())
                    {
                        if (!int.TryParse(entry.Name, out int gid)) continue;
                        var tabla = new Dictionary<int, int>();
                        foreach (var v in entry.Value.EnumerateObject())
                        {
                            if (int.TryParse(v.Name, out int índice) && v.Value.TryGetInt32(out int hueco))
                                tabla[índice] = hueco;
                        }
                        _slotsByVariant[gid] = tabla;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Apariencias] No se pudo leer el aspecto de las prendas: {ex.Message}");
            }
        }

        /// <summary>Los aspectos que no van por pieles: mascotas y monturas.</summary>
        private static void ReadLooks(JsonElement root, string name, Dictionary<int, PieceLook> into)
        {
            if (!root.TryGetProperty(name, out var block)) return;
            foreach (var entry in block.EnumerateObject())
            {
                if (!int.TryParse(entry.Name, out int gid)) continue;

                byte[]? colores = null;
                bool delPortador = false;
                if (entry.Value.TryGetProperty("c", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    string texto = c.GetString() ?? "";
                    if (texto == "portador") delPortador = true;
                    else colores = FromHex(texto);
                }

                into[gid] = new PieceLook
                {
                    Bones = entry.Value.TryGetProperty("b", out var b) ? b.GetInt32() : 0,
                    Scale = entry.Value.TryGetProperty("s", out var s) ? s.GetInt32() : 0,
                    Skin = entry.Value.TryGetProperty("p", out var p) ? p.GetInt32() : 0,
                    Colors = colores,
                    ColorsFromWearer = delPortador,
                };
            }
        }

        private static void ReadIds(JsonElement root, string name, List<int> into)
        {
            if (!root.TryGetProperty(name, out var block) || block.ValueKind != JsonValueKind.Array) return;
            foreach (var v in block.EnumerateArray())
            {
                if (v.TryGetInt32(out int id)) into.Add(id);
            }
        }

        private static byte[]? FromHex(string texto)
        {
            if (string.IsNullOrEmpty(texto) || texto.Length % 2 != 0) return null;
            try { return Convert.FromHexString(texto); }
            catch { return null; }
        }

        /// <summary>Una piel viene como número; varias, como lista.</summary>
        private static int[] ReadSkinValue(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Array)
            {
                var lista = new List<int>();
                foreach (var v in value.EnumerateArray())
                {
                    if (v.TryGetInt32(out int piel)) lista.Add(piel);
                }
                return lista.ToArray();
            }
            return value.TryGetInt32(out int única) ? new[] { única } : Array.Empty<int>();
        }

        private static void ReadSkins(JsonElement root, string name, Dictionary<int, int[]> into)
        {
            if (!root.TryGetProperty(name, out var block)) return;
            foreach (var entry in block.EnumerateObject())
            {
                if (!int.TryParse(entry.Name, out int key)) continue;
                var pieles = ReadSkinValue(entry.Value);
                if (pieles.Length > 0) into[key] = pieles;
            }
        }

        private static void ReadPairs(JsonElement root, string name, Dictionary<int, int> into)
        {
            if (!root.TryGetProperty(name, out var block)) return;
            foreach (var entry in block.EnumerateObject())
            {
                if (int.TryParse(entry.Name, out int key) && entry.Value.TryGetInt32(out int value))
                {
                    into[key] = value;
                }
            }
        }

        public static bool Exists(int gid) => _catalogue.ContainsKey(gid);
        public static Piece? Of(int gid) => _catalogue.TryGetValue(gid, out var p) ? p : null;

        /// <summary>
        /// El hueco que le toca a una prenda. Es lo que el servidor devuelve en el lwz.
        ///
        /// Manda lo MEDIDO, y por variante antes que por objeto: una sortija viviente imita una capa
        /// con una variante y un sombrero con otra. Solo si no hay medida se cae al tipo, que para
        /// las armas y los objetos vivientes acertaría poco.
        /// </summary>
        public static int SlotOf(int gid, int variant = 0)
        {
            if (_slotsByVariant.TryGetValue(gid, out var porVariante))
            {
                if (porVariante.TryGetValue(variant, out int medido)) return medido;
                if (variant == 0 && porVariante.Count > 0)
                {
                    foreach (var v in porVariante) return v.Value;   // la primera que se midió
                }
            }
            if (_slots.TryGetValue(gid, out int slot)) return slot;

            var piece = Of(gid);
            if (piece == null) return -1;
            return SlotOfType.TryGetValue(piece.Type, out int porTipo) ? porTipo : -1;
        }

        /// <summary>
        /// Las pieles que mete una prenda, vacío si no las sabemos. Casi siempre es una sola; los
        /// objetos vivientes cambian según la variante elegida.
        /// </summary>
        public static IReadOnlyList<int> SkinsOf(int gid, int variant)
        {
            if (_variants.TryGetValue(gid, out var tabla))
            {
                if (tabla.TryGetValue(variant, out var deVariante)) return deVariante;
                // Una variante que no está medida no debe caer en la piel de otra: mejor nada.
                if (variant != 0) return _ninguna;
            }
            return _skins.TryGetValue(gid, out var pieles) ? pieles : _ninguna;
        }

        /// <summary>
        /// El aspecto que impone una mascotura o una montura de apariencia sobre la raíz, o null.
        /// Las dos van al hueco 5 y las dos sustituyen a la montura; la diferencia es que la de
        /// apariencia trae además su propia piel.
        /// </summary>
        public static PieceLook? MountLookOf(int gid) => _mounts.TryGetValue(gid, out var m) ? m : null;

        /// <summary>La subentidad de una mascota de apariencia, o null.</summary>
        public static PieceLook? PetOf(int gid) => _pets.TryGetValue(gid, out var p) ? p : null;

        /// <summary>Los huesos de un aura, o cero.</summary>
        public static int AuraBones(int auraId) => _auras.TryGetValue(auraId, out int b) ? b : 0;

        /// <summary>Los huesos de una apariencia de tipo 5, o cero si no es compatible.</summary>
        public static int AppearanceBones(int appearanceId)
            => _appearanceBones.TryGetValue(appearanceId, out int bones) ? bones : 0;
    }
}
