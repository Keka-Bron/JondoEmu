using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// El aspecto del EQUIPO DE VERDAD que se lleva puesto: qué piel mete cada objeto real
    /// (arma, sombrero, capa...) en el f6 del aspecto del personaje.
    ///
    /// No confundir con <see cref="Cosmetics"/>, que es de las prendas de apariencia (Merkasako):
    /// aquellas se indexan por el gid de la prenda cosmética, y esto por el ID DE PLANTILLA del
    /// objeto real (ItemTemplates.Id), medido sobre las capturas del servidor de torneos con
    /// tools/extraer_equipo_real.py. Ver equipment_skins.json.
    /// </summary>
    public static class EquipmentSkins
    {
        private static readonly Dictionary<int, int> _skins = new Dictionary<int, int>();
        private static bool _loaded;
        private static readonly object _lock = new object();

        public static int Count => _skins.Count;

        public static void Initialize()
        {
            lock (_lock)
            {
                _skins.Clear();
                Load();
                _loaded = true;
                Console.WriteLine($"[Equipo] {_skins.Count} objetos reales con su piel.");
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                Load();
                _loaded = true;
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

        /// <summary>La piel que mete ese objeto real, o cero si no la tenemos medida.</summary>
        public static int SkinOf(int templateId)
        {
            EnsureLoaded();
            return _skins.TryGetValue(templateId, out int skin) ? skin : 0;
        }
    }
}
