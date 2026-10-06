using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Appearance garments: what exists and what each one looks like.
    ///
    /// There are two files and they should not be confused:
    ///
    ///   cosmetics.json       the CATALOGUE, taken from the client: 2,409 items spread over the 12
    ///                        types the client marks as category 5 (hats, capes, shields, outfits,
    ///                        wings, shoulder pads, pets, petsmounts and so on).
    ///   cosmetic_skins.json  each one's LOOK, which is NOT in the client and was measured by
    ///                        comparing the look block before and after equipping in the real
    ///                        captures.
    ///
    /// From the captures it is also known how each kind of garment changes the look, and not all do it
    /// the same:
    ///
    ///   a cape, a hat, a shield, an outfit, wings or shoulder pads PUT A NUMBER in the list of
    ///   skins (the f6) -- in the real game they replace the real garment's, here it is added because
    ///   we never knew the real garment's;
    ///   a petsmount CHANGES THE ROOT'S BONES, and does not touch the skins;
    ///   a pet HANGS a subentity from attachment 1;
    ///   an aura, another from attachment 6.
    /// </summary>
    public static class Cosmetics
    {
        /// <summary>The appearance window's slots, from the captures.</summary>
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
        /// The slot each item type goes to. It acts as a net: when the slot is MEASURED in the captures the
        /// measured one rules, because for two families this table cannot get it right. The 194 appearance
        /// weapons are all of the same type and spread over ten slots (one per type of real weapon
        /// imitated), and a living item changes slot depending on the variant chosen for it.
        /// </summary>
        private static readonly Dictionary<int, int> SlotOfType = new Dictionary<int, int>
        {
            { 246, SlotHat },        // appearance hat
            { 247, SlotCape },       // capa
            { 248, SlotShield },     // escudo
            { 249, SlotPet },        // mascota
            { 250, SlotMount },      // mascotura
            { 324, SlotMount },      // appearance mount
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
        /// The look a garment that does not go through skins imposes: a pet, which hangs from attachment 1,
        /// or a petsmount/mount, which rules the root.
        ///
        /// A missing SCALE is the default one, not zero: it travels as a packed repeated and a zero would be
        /// encoded explicitly, so zero here means "do not touch it".
        /// </summary>
        public sealed class PieceLook
        {
            public int Bones { get; init; }
            public int Scale { get; init; }
            /// <summary>The root's skin; only appearance mounts set it.</summary>
            public int Skin { get; init; }
            /// <summary>The measured colours, already packed. Empty if the garment does not touch them.</summary>
            public byte[]? Colors { get; init; }
            /// <summary>The colour is that of the CHARACTER wearing it, copied byte for byte.</summary>
            public bool ColorsFromWearer { get; init; }
        }

        private static readonly Dictionary<int, Piece> _catalogue = new Dictionary<int, Piece>();
        // Nearly every garment puts in ONE skin, but there are three measured ones that put in two --
        // cape 18579, shield 13240 and outfit 18525 --, so the value is a list.
        private static readonly Dictionary<int, int[]> _skins = new Dictionary<int, int[]>();
        private static readonly Dictionary<int, Dictionary<int, int[]>> _variants
            = new Dictionary<int, Dictionary<int, int[]>>();
        private static readonly int[] _ninguna = Array.Empty<int>();
        private static readonly Dictionary<int, PieceLook> _mounts = new Dictionary<int, PieceLook>();
        private static readonly Dictionary<int, PieceLook> _pets = new Dictionary<int, PieceLook>();
        private static readonly Dictionary<int, int> _auras = new Dictionary<int, int>();
        // Measured slots: the weapons' go by item, the living items' by (item, variant), because
        // the same ring imitates a cape or a hat depending on which is chosen.
        private static readonly Dictionary<int, int> _slots = new Dictionary<int, int>();
        private static readonly Dictionary<int, Dictionary<int, int>> _slotsByVariant
            = new Dictionary<int, Dictionary<int, int>>();
        private static readonly List<int> _titles = new List<int>();
        private static readonly List<int> _ornaments = new List<int>();
        private static readonly Dictionary<int, int> _appearanceBones = new Dictionary<int, int>();

        /// <summary>
        /// Whether the tables are filled in and safe to read. Volatile, and raised LAST: see
        /// <see cref="Ensure"/>.
        /// </summary>
        private static volatile bool _loaded;
        private static readonly object _lock = new object();

        public static int Count { get { Ensure(); return _catalogue.Count; } }
        public static int KnownLooks
        {
            get { Ensure(); return _skins.Count + _mounts.Count + _pets.Count + _variants.Count; }
        }
        public static IEnumerable<KeyValuePair<int, Piece>> All { get { Ensure(); return _catalogue; } }
        /// <summary>The titles and ornaments the real server accepted in the captures.</summary>
        public static IReadOnlyList<int> MeasuredTitles { get { Ensure(); return _titles; } }
        public static IReadOnlyList<int> MeasuredOrnaments { get { Ensure(); return _ornaments; } }

        /// <summary>
        /// Reads the two files, once per run. Kept as a separate call so the server can pay for it
        /// at boot, with its log line, instead of on whoever happens to equip something first.
        /// </summary>
        /// <remarks>
        /// CALLING IT AGAIN DOES NOTHING, ON PURPOSE. It used to clear all eleven tables and refill
        /// them, with no lock of any kind, and two callers at once tore the dictionaries apart --
        /// "Operations that change non-concurrent collections must have exclusive access", thrown
        /// from inside Initialize itself. Everyone reading a look at that moment saw the same
        /// wreckage, which is how one racy loader failed tests that had nothing to do with it.
        ///
        /// Reloading was never the point anyway: cosmetics.json and cosmetic_skins.json are
        /// measurements that do not change while the server is up, and there was nothing to gain
        /// from reading them twice.
        /// </remarks>
        public static void Initialize() => Ensure();

        private static void Ensure()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                try
                {
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
                finally
                {
                    // Raised last, so that the fast path above never waves a reader through onto
                    // half-built tables; and in a finally so a missing file counts as tried.
                    _loaded = true;
                }
            }
        }

        /// <summary>
        /// The titles and ornaments the real server accepted have to be among those offered. If
        /// titles_ornaments.json is ever regenerated and one is lost, this says so instead of leaving a title
        /// that exists but cannot be put on.
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

                // Effect 335 carries an appearance identifier, not the bones directly.
                // Type 5 ones are simple skeleton replacements: in 3.6.10.10 the Ouginak's bestial
                // form is appearance 1260, which points at bones 9025.
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

        /// <summary>The looks that do not go through skins: pets and mounts.</summary>
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

        /// <summary>A skin comes as a number; several, as a list.</summary>
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

        // Every reader goes through Ensure first. Before, a caller that had not thought to call
        // Initialize got empty tables and a character with nothing on -- silently, which is why
        // the launcher shot tests had to remember to initialize by hand.
        public static bool Exists(int gid) { Ensure(); return _catalogue.ContainsKey(gid); }

        /// <summary>Every appearance mount and pet-mount whose look is measured.</summary>
        public static IReadOnlyCollection<PieceLook> MountLooks { get { Ensure(); return _mounts.Values; } }

        /// <summary>
        /// The slot a cosmetic's skin dresses, as the real item type it stands for -- hat 16, cape
        /// 17, shield 82 -- or zero.
        /// </summary>
        public static int ItemTypeOfSkin(int skin)
        {
            Ensure();
            foreach (var (gid, skins) in _skins)
            {
                if (Array.IndexOf(skins, skin) < 0 || !_catalogue.TryGetValue(gid, out var piece)) continue;
                return piece.Type switch { 246 => 16, 247 => 17, 248 => 82, _ => 0 };
            }
            return 0;
        }
        public static Piece? Of(int gid)
        {
            Ensure();
            return _catalogue.TryGetValue(gid, out var p) ? p : null;
        }

        /// <summary>
        /// The slot a garment gets. It is what the server returns in the lwz.
        ///
        /// The MEASURED one rules, and by variant before by item: a living ring imitates a cape with one
        /// variant and a hat with another. Only if there is no measurement does it fall back to the type,
        /// which for weapons and living items would rarely be right.
        /// </summary>
        public static int SlotOf(int gid, int variant = 0)
        {
            Ensure();
            if (_slotsByVariant.TryGetValue(gid, out var porVariante))
            {
                if (porVariante.TryGetValue(variant, out int medido)) return medido;
                if (variant == 0 && porVariante.Count > 0)
                {
                    foreach (var v in porVariante) return v.Value;   // the first one measured
                }
            }
            if (_slots.TryGetValue(gid, out int slot)) return slot;

            var piece = Of(gid);
            if (piece == null) return -1;
            return SlotOfType.TryGetValue(piece.Type, out int porTipo) ? porTipo : -1;
        }

        /// <summary>
        /// The skins a garment puts in, empty if we do not know them. It is nearly always a single one;
        /// living items change depending on the chosen variant.
        /// </summary>
        public static IReadOnlyList<int> SkinsOf(int gid, int variant)
        {
            Ensure();
            if (_variants.TryGetValue(gid, out var tabla))
            {
                if (tabla.TryGetValue(variant, out var deVariante)) return deVariante;
                // A variant that is not measured must not fall into another's skin: better nothing.
                if (variant != 0) return _ninguna;
            }
            return _skins.TryGetValue(gid, out var pieles) ? pieles : _ninguna;
        }

        /// <summary>
        /// The look a petsmount or an appearance mount imposes on the root, or null. Both go to slot 5 and
        /// both replace the mount; the difference is that the appearance one also brings its own skin.
        /// </summary>
        public static PieceLook? MountLookOf(int gid)
        {
            Ensure();
            return _mounts.TryGetValue(gid, out var m) ? m : null;
        }

        /// <summary>An appearance pet's subentity, or null.</summary>
        public static PieceLook? PetOf(int gid)
        {
            Ensure();
            return _pets.TryGetValue(gid, out var p) ? p : null;
        }

        /// <summary>An aura's bones, or zero.</summary>
        public static int AuraBones(int auraId)
        {
            Ensure();
            return _auras.TryGetValue(auraId, out int b) ? b : 0;
        }

        /// <summary>A type 5 appearance's bones, or zero if it is not compatible.</summary>
        public static int AppearanceBones(int appearanceId)
        {
            Ensure();
            return _appearanceBones.TryGetValue(appearanceId, out int bones) ? bones : 0;
        }
    }
}
