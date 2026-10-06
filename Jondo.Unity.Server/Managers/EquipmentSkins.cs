using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The look of the REAL EQUIPMENT worn: which skin each real item (weapon, hat, cape...) puts into the
    /// f6 of the character's look.
    ///
    /// Not to be confused with <see cref="Cosmetics"/>, which is about appearance garments (Merkasako):
    /// those are indexed by the cosmetic garment's gid, and this by the real item's TEMPLATE ID
    /// (ItemTemplates.Id), measured on the tournament server's captures with
    /// tools/extraer_equipo_real.py. See equipment_skins.json.
    /// </summary>
    public static class EquipmentSkins
    {
        private static readonly Dictionary<int, int> _skins = new Dictionary<int, int>();
        /// <summary>
        /// Whether the table is filled in and safe to read. Volatile because the fast path in
        /// <see cref="EnsureLoaded"/> reads it outside the lock.
        /// </summary>
        private static volatile bool _loaded;
        private static readonly object _lock = new object();

        public static int Count { get { EnsureLoaded(); return _skins.Count; } }

        /// <summary>
        /// Reads the file, once per run. Kept as a separate call so the server can pay for it at
        /// boot, with its log line, rather than on whoever builds the first look.
        /// </summary>
        /// <remarks>
        /// CALLING IT AGAIN DOES NOTHING, ON PURPOSE. It used to clear the table and read the file
        /// afresh, and while it did that -- holding a lock no reader takes -- everybody building a
        /// look saw an empty table, or worse, one being written under them. equipment_skins.json is
        /// a measurement that does not change while the server is up, so there was nothing to
        /// reload.
        /// </remarks>
        public static void Initialize() => EnsureLoaded();

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                try
                {
                    Load();
                    Console.WriteLine($"[Equipo] {_skins.Count} objetos reales con su piel.");
                }
                finally
                {
                    // Raised last, so the fast path above never lets a reader onto a half-filled
                    // table; and in a finally so a missing file counts as tried.
                    _loaded = true;
                }
            }
        }

        private static void Load()
        {
            string path = Paths.EquipmentSkinsJson;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Equipo] Falta {Path.GetFileName(path)}.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("skins", out var skins)) return;

                // THE FILE FLAGS ITS OWN DOUBTFUL ROWS, AND THEY WERE GOING LIVE ANYWAY.
                //
                // The table has three lists: 286 entries measured off the captures, 455 inferred
                // by image matching, and 82 the author put in "_inferred_needs_review" precisely
                // because the match was not good enough to trust. The commit that brought them
                // says the two confidence levels are "kept apart on purpose" — and then the
                // loader read all 823 without looking at the lists, so the 82 shipped.
                //
                // A wrong skin is not a crash: the character just wears somebody else's hat, and
                // nobody can tell it from a bug in the look pipeline. Which is exactly why they
                // stay out until somebody measures them.
                var dudosas = new HashSet<int>();
                if (doc.RootElement.TryGetProperty("_inferred_needs_review", out var revisar) &&
                    revisar.ValueKind == JsonValueKind.Array)
                {
                    foreach (var id in revisar.EnumerateArray())
                    {
                        if (id.TryGetInt32(out int cual)) dudosas.Add(cual);
                    }
                }

                int saltadas = 0;
                foreach (var entry in skins.EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out int templateId)) continue;
                    if (dudosas.Contains(templateId)) { saltadas++; continue; }
                    if (entry.Value.TryGetInt32(out int skinId)) _skins[templateId] = skinId;
                }

                if (saltadas > 0)
                {
                    Console.WriteLine($"[Equipo] {saltadas} pieles marcadas para revisar se quedan " +
                                      "fuera hasta que alguien las mida.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Equipo] No se pudo leer el aspecto del equipo: {ex.Message}");
            }
        }

        /// <summary>The item type -- hat 16, cape 17, shield 82 -- whose piece puts this skin on, or zero.</summary>
        public static int TypeOfSkin(int skin)
        {
            EnsureLoaded();
            var byType = _typeOfSkin;
            if (byType == null)
            {
                byType = new Dictionary<int, int>();
                foreach (var (template, itsSkin) in _skins)
                {
                    int type = Forgemagic.TemplateOf(template)?.Type ?? 0;
                    if (type != 0) byType.TryAdd(itsSkin, type);
                }
                _typeOfSkin = byType;
            }
            return byType.TryGetValue(skin, out int found) ? found : 0;
        }

        private static Dictionary<int, int>? _typeOfSkin;

        /// <summary>Every real item with its skin measured.</summary>
        public static IReadOnlyDictionary<int, int> All { get { EnsureLoaded(); return _skins; } }

        /// <summary>The skin that real item puts in, or zero if we do not have it measured.</summary>
        public static int SkinOf(int templateId)
        {
            EnsureLoaded();
            return _skins.TryGetValue(templateId, out int skin) ? skin : 0;
        }
    }
}
