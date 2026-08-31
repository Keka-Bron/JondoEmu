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

        /// <summary>Una lectura: el documento y, si la tiene, su pregunta de aceptar.</summary>
        public sealed class Readable
        {
            public int Document { get; init; }

            /// <summary>La frase de la pregunta. Cero cuando la lectura no pregunta nada.</summary>
            public int Question { get; init; }

            /// <summary>La respuesta que acepta. Es la que deja constancia de haber leido.</summary>
            public int Accept { get; init; }

            /// <summary>La que se marcha sin aceptar. Opcional.</summary>
            public int Decline { get; init; }

            public bool Asks => Question != 0 && Accept != 0;
        }

        private static readonly Dictionary<(long Map, int Element), Readable> _porElemento
            = new Dictionary<(long, int), Readable>();

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
                    _porElemento[(map, element)] = new Readable
                    {
                        Document = document,
                        Question = (int)Number(row, "question"),
                        Accept = (int)Number(row, "accept"),
                        Decline = (int)Number(row, "decline"),
                    };
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
        public static Readable? Of(long mapId, int elementId)
        {
            if (_porElemento.TryGetValue((mapId, elementId), out var lectura)) return lectura;
            return _porElemento.TryGetValue((0, elementId), out lectura) ? lectura : null;
        }

        /// <summary>La lectura cuya respuesta de aceptar es esta, o null.</summary>
        /// <remarks>
        /// Hace falta para atender el ioy: la respuesta llega suelta, sin decir de que elemento
        /// venia, y es la unica manera de saber que se acaba de aceptar una oferta.
        /// </remarks>
        public static (long Map, int Element, Readable Lectura)? ByAcceptReply(long reply)
        {
            foreach (var ((map, element), lectura) in _porElemento)
            {
                if (lectura.Accept != 0 && lectura.Accept == reply) return (map, element, lectura);
            }
            return null;
        }

        /// <summary>Los elementos de este mapa que abren un documento.</summary>
        /// <remarks>
        /// Lo necesita la lista de actores: sin declararlos ahí el cliente no los pinta como
        /// pulsables, y entonces da igual lo bien que el servidor conteste al clic, porque no
        /// llega ninguno. Incluye los de mapa cero, que valen en cualquiera.
        /// </remarks>
        public static IEnumerable<int> OnMap(long mapId)
        {
            foreach (var ((map, element), _) in _porElemento)
            {
                if (map == mapId || map == 0) yield return element;
            }
        }

        private static long Number(JsonElement row, string name)
            => row.TryGetProperty(name, out var value) && value.TryGetInt64(out long number) ? number : 0;
    }
}
