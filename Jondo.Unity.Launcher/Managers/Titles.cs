using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// Los títulos y los ornamentos que existen en el juego.
    ///
    /// El título es el texto que sale bajo el nombre; el ornamento, el marco que lo rodea. El
    /// cliente ya tiene el catálogo entero en sus datos —539 títulos y 167 ornamentos— y lo que
    /// espera del servidor es solo la lista de los que uno TIENE, que manda una vez al entrar:
    ///
    ///   hhy { f1: [títulos], f2: [ornamentos] }     los dos empaquetados
    ///
    /// Lo que no está en esa lista lo pinta en gris. Aquí van todos, que es lo que se pide.
    ///
    /// Los ids salen de titles_ornaments.json, que genera tools/extract_titulos.py leyendo las
    /// tablas `titles` y `ornaments` del cliente.
    /// </summary>
    public static class Titles
    {
        private static readonly List<long> _titles = new List<long>();
        private static readonly List<long> _ornaments = new List<long>();

        public static IReadOnlyList<long> All => _titles;
        public static IReadOnlyList<long> AllOrnaments => _ornaments;

        public static void Initialize()
        {
            _titles.Clear();
            _ornaments.Clear();

            string path = Paths.TitlesOrnamentsJson;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Títulos] Falta {Path.GetFileName(path)}; no habrá ni títulos ni " +
                                  "ornamentos. Genéralo con tools/extract_titulos.py.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                Read(doc.RootElement, "titles", _titles);
                Read(doc.RootElement, "ornaments", _ornaments);

                Console.WriteLine($"[Títulos] {_titles.Count} títulos y {_ornaments.Count} ornamentos.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Títulos] No se pudo leer {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        private static void Read(JsonElement root, string name, List<long> into)
        {
            if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return;
            foreach (var entry in array.EnumerateArray())
            {
                if (entry.TryGetInt64(out long id)) into.Add(id);
            }
        }

        public static bool HasTitle(int id) => id == Wardrobe.None || _titles.Contains(id);
        public static bool HasOrnament(int id) => id == Wardrobe.None || _ornaments.Contains(id);
    }
}
