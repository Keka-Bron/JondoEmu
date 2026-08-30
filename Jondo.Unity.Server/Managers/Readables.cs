using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Los interactivos que al pulsarlos abren un documento: carteles, libros, placas.
    /// </summary>
    /// <remarks>
    /// El cliente los enseña con un <c>kkt</c> que sólo lleva el id del documento; ni el título ni
    /// el texto viajan, los tiene él en su propia tabla. Está medido en la captura de abrir el
    /// libro del escudo de Feca: el cliente manda <c>iuu</c> y el servidor contesta
    /// <c>kkt { f2: 217 }</c>.
    ///
    /// Vive en un fichero aparte y no en las ataduras de misión porque no es una cosa de misiones:
    /// un cartel se puede leer con o sin misión, y lo que hace es dejar constancia de que se ha
    /// leído. Que esa constancia luego abra una respuesta de un NPC es asunto del diálogo.
    /// </remarks>
    public static class Readables
    {
        public const string File = "world/readables.json";

        private static readonly Dictionary<(long Map, int Element), int> _porElemento
            = new Dictionary<(long, int), int>();

        public static int Count => _porElemento.Count;

        public static void Load()
        {
            _porElemento.Clear();
            string path = Paths.ContentFile(File);

            if (!System.IO.File.Exists(path))
            {
                Console.WriteLine($"[Lecturas] No está {File}: ningún cartel se podrá leer.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("readables", out var list)
                    || list.ValueKind != JsonValueKind.Array)
                {
                    return;
                }

                foreach (var row in list.EnumerateArray())
                {
                    long map = Number(row, "map");
                    int element = (int)Number(row, "element");
                    int document = (int)Number(row, "document");

                    if (element == 0 || document == 0) continue;
                    _porElemento[(map, element)] = document;
                }

                Console.WriteLine($"[Lecturas] {_porElemento.Count} interactivo(s) con documento.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Lecturas] No se pudo leer {File}: {ex.Message}");
            }
        }

        /// <summary>El documento de este elemento, o cero si no abre ninguno.</summary>
        /// <remarks>
        /// Se pregunta primero por el mapa exacto y después con mapa cero, que quiere decir «en
        /// cualquiera». El cartel de la taberna lleva el MISMO id de elemento en los dos mapas
        /// contiguos, porque el edificio ocupa las dos casillas, y sin la segunda pregunta habría
        /// que escribir la misma fila dos veces.
        /// </remarks>
        public static int DocumentOf(long mapId, int elementId)
        {
            if (_porElemento.TryGetValue((mapId, elementId), out int document)) return document;
            return _porElemento.TryGetValue((0, elementId), out document) ? document : 0;
        }

        private static long Number(JsonElement row, string name)
            => row.TryGetProperty(name, out var value) && value.TryGetInt64(out long number) ? number : 0;
    }
}
