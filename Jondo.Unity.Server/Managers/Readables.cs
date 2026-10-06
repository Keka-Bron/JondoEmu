using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The interactives that open a document when pressed: signs, books, plaques.
    /// </summary>
    /// <remarks>
    /// The client shows them with a <c>kkt</c> that only carries the document's id; neither the title nor
    /// the text travel, it has them in its own table. It is measured in the capture of opening the
    /// Feca shield book: the client sends <c>iuu</c> and the server answers
    /// <c>kkt { f2: 217 }</c>.
    ///
    /// It lives in a separate file and not in the quest bindings because it is not a quest thing:
    /// a sign can be read with or without a quest, and what it does is leave a record that it has been
    /// read. That this record then opens an NPC's reply is the dialogue's business.
    /// </remarks>
    public static class Readables
    {
        public const string File = "world/readables.json";

        /// <summary>A reading: the document and, if it has one, its accept question.</summary>
        public sealed class Readable
        {
            public int Document { get; init; }

            /// <summary>The question's sentence. Zero when the reading asks nothing.</summary>
            public int Question { get; init; }

            /// <summary>The reply that accepts. It is the one that leaves a record of having read.</summary>
            public int Accept { get; init; }

            /// <summary>The one that leaves without accepting. Optional.</summary>
            public int Decline { get; init; }

            public bool Asks => Question != 0 && Accept != 0;
        }

        private static readonly Dictionary<(long Map, int Element), Readable> _porElemento
            = new Dictionary<(long, int), Readable>();

        /// <summary>
        /// Whether the signs are read in and safe to use. Volatile because the fast path in
        /// <see cref="Ensure"/> reads it outside the lock, and raised LAST.
        /// </summary>
        private static volatile bool _loaded;
        private static readonly object _lock = new object();

        public static int Count { get { Ensure(); return _porElemento.Count; } }

        /// <summary>
        /// Reads the file, once per run. Kept as a separate call so the server pays for it at
        /// boot, with its log line, and not on the first sign somebody reads.
        /// </summary>
        public static void Load() => Ensure();

        private static void Ensure()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                try
                {
                    Read();
                }
                finally
                {
                    _loaded = true;   // in a finally so a missing file counts as tried
                }
            }
        }

        private static void Read()
        {
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

        /// <summary>This element's document, or zero if it opens none.</summary>
        /// <remarks>
        /// It is asked first by the exact map and then with map zero, which means «on
        /// any». The tavern sign carries the SAME element id on the two adjacent
        /// maps, because the building takes up both cells, and without the second question the
        /// same row would have to be written twice.
        /// </remarks>
        public static Readable? Of(long mapId, int elementId)
        {
            if (_porElemento.TryGetValue((mapId, elementId), out var lectura)) return lectura;
            return _porElemento.TryGetValue((0, elementId), out lectura) ? lectura : null;
        }

        /// <summary>The reading whose accept reply is this one, or null.</summary>
        /// <remarks>
        /// It is needed to handle the ioy: the reply arrives on its own, without saying which element
        /// it came from, and it is the only way of knowing that an offer has just been accepted.
        /// </remarks>
        public static (long Map, int Element, Readable Lectura)? ByAcceptReply(long reply)
        {
            foreach (var ((map, element), lectura) in _porElemento)
            {
                if (lectura.Accept != 0 && lectura.Accept == reply) return (map, element, lectura);
            }
            return null;
        }

        /// <summary>The elements of this map that open a document.</summary>
        /// <remarks>
        /// The actor list needs it: without declaring them there the client does not draw them as
        /// pressable, and then however well the server answers the click does not matter, because none
        /// arrives. It includes the map-zero ones, which hold on any.
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
