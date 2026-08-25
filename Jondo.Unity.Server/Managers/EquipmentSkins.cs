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
                Console.WriteLine($"[Equipo] {_skins.Count} objetos reales con su piel medida.");
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

                foreach (var entry in skins.EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out int templateId)) continue;
                    if (entry.Value.TryGetInt32(out int skinId)) _skins[templateId] = skinId;
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
