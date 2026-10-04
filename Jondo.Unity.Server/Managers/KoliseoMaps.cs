using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Las arenas del koliseo, con sus casillas de colocación por bando.
    /// </summary>
    /// <remarks>
    /// Un koliseo no se pelea en el arena que le tocaría al mapa de rol: se pelea en una de las
    /// arenas del koliseo, y el juego elige una al azar. Son <b>441 identificadores de mapa</b> en
    /// tres subáreas, contados sobre <c>MapSubareas</c>:
    ///
    /// <code>
    ///    885  Koliseo - Duelo            85 mapas
    ///   1122  Koliseo - Equipos          88
    ///   1123  Koliseo - Entrenamiento   268
    /// </code>
    ///
    /// El nombre de la subárea es un identificador numérico que hay que resolver contra
    /// <c>Translations</c>; por eso buscar el texto «koliseo» en la tabla de subáreas no encuentra
    /// nada. Y no son 441 arenas distintas: por casillas de colocación se reducen a <b>101 diseños</b>,
    /// la mayoría con cinco copias.
    ///
    /// <b>EL TAMAÑO IMPORTA, y por eso no se elige por subárea.</b> Las de Duelo son pequeñas de
    /// verdad —37 de 85 sólo tienen una casilla por bando y 77 de 85 no admiten tres— mientras que
    /// las de Equipos nunca bajan de cuatro. Elegir «la subárea que le toca» pondría un 3 contra 3
    /// en un mapa con sitio para uno. Se elige por CAPACIDAD, que es el mínimo de las dos listas, y
    /// entonces el uno contra uno cae solo en las pequeñas y el tres contra tres en las grandes.
    ///
    /// Las casillas salen del propio cliente, de las banderas <c>red</c> y <c>blue</c> de
    /// <c>cellsData[]</c>, y están comprobadas contra el kba del servidor real en el mapa
    /// 233308168 de la captura del 2 contra 2: los mismos dos conjuntos.
    ///
    /// <b>Ojo con los nombres, que se cruzan.</b> El kba manda el equipo 0 en su f1, y ese f1 es la
    /// lista que el cliente llama <c>red</c>. Así que las rojas del cliente son nuestro equipo AZUL.
    /// El fichero guarda los nombres del cliente y la traducción se hace aquí, una sola vez.
    /// </remarks>
    public static class KoliseoMaps
    {
        public sealed class Arena
        {
            public long MapId { get; init; }
            public int SubAreaId { get; init; }
            public string Name { get; init; } = "";

            /// <summary>Las del equipo azul. Son las que el cliente llama rojas.</summary>
            public List<int> Blue { get; init; } = new List<int>();

            /// <summary>Las del equipo rojo. Las que el cliente llama azules.</summary>
            public List<int> Red { get; init; } = new List<int>();

            /// <summary>Cuánta gente cabe por bando: lo que decide a qué modalidad sirve.</summary>
            public int Capacity => Math.Min(Blue.Count, Red.Count);
        }

        private static readonly List<Arena> _arenas = new List<Arena>();
        private static readonly object _lock = new object();
        private static readonly Random _azar = new Random();
        /// <summary>
        /// Whether the list is filled in and safe to read. Volatile, and raised LAST: see
        /// <see cref="EnsureLoadedLocked"/>.
        /// </summary>
        private static volatile bool _loaded;

        /// <summary>The maps of the file left out for having no name: see the load.</summary>
        private static int _sinNombre;

        public static int Count
        {
            get { EnsureLoaded(); return _arenas.Count; }
        }

        /// <summary>Cuántas arenas admiten esa gente por bando.</summary>
        public static int CountFor(int teamSize)
        {
            EnsureLoaded();
            int n = 0;
            foreach (var arena in _arenas) if (arena.Capacity >= teamSize) n++;
            return n;
        }

        /// <summary>
        /// Reads the arenas, once per run. Kept as a separate call so the server pays for it at
        /// boot, with its log line, rather than on the first fight looking for an arena.
        /// </summary>
        /// <remarks>
        /// Calling it again does nothing, on purpose. It used to clear the list, drop the flag and
        /// rebuild -- and readers walk _arenas without the lock, so anyone counting arenas during
        /// that window counted the wrong number, or walked a list being added to.
        /// </remarks>
        public static void Initialize()
        {
            bool first = !_loaded;
            EnsureLoaded();
            if (!first) return;

            Console.WriteLine($"[Koliseo] {_arenas.Count} arenas: {CountFor(1)} para uno contra uno, " +
                              $"{CountFor(2)} para dos contra dos, {CountFor(3)} para tres contra tres " +
                              $"({_sinNombre} sin nombre, fuera).");
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock) EnsureLoadedLocked();
        }

        /// <remarks>
        /// The flag goes up after the load and not before, for the same reason as in
        /// <see cref="BreedLookTable"/>: raised on entry, the lock-free fast path in
        /// <see cref="EnsureLoaded"/> lets other threads walk the list while this one is still
        /// adding to it, and an arena that is there does not get counted.
        /// </remarks>
        private static void EnsureLoadedLocked()
        {
            if (_loaded) return;

            string path = Paths.KoliseoMapsJson;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Koliseo] Falta {Path.GetFileName(path)}: los combates irán al " +
                                  "arena de siempre.");
                _loaded = true;
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("mapas", out var mapas)) return;

                foreach (var entrada in mapas.EnumerateArray())
                {
                    var arena = new Arena
                    {
                        MapId = entrada.GetProperty("id").GetInt64(),
                        SubAreaId = entrada.TryGetProperty("subarea", out var sa) ? sa.GetInt32() : 0,
                        Name = entrada.TryGetProperty("nombre", out var nm) ? (nm.GetString() ?? "") : "",
                    };

                    // Las rojas del cliente son nuestro azul, y al revés. Ver el comentario de arriba.
                    Leer(entrada, "rojas", arena.Blue);
                    Leer(entrada, "azules", arena.Red);

                    // Only the named ones are arenas the game fights on. The 46 without a name are
                    // leftovers, and three of them (230170117, 230432261, 230694405) are not even
                    // a board: every one of their 522 cells walks in a fight, where the drawn
                    // board has 253 -- the client itself lights up cells out in the void, and
                    // titles the map "Amakna 0,0" because it does not know it.
                    if (arena.Name.Length == 0) { _sinNombre++; continue; }

                    if (arena.Capacity > 0) _arenas.Add(arena);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Koliseo] No se pudo leer {Path.GetFileName(path)}: {ex.Message}");
            }
            finally
            {
                // In a finally so a broken file still counts as tried, and we do not go back to
                // the disk for every fight looking for an arena.
                _loaded = true;
            }
        }

        private static void Leer(JsonElement entrada, string campo, List<int> donde)
        {
            if (!entrada.TryGetProperty(campo, out var lista) ||
                lista.ValueKind != JsonValueKind.Array)
            {
                return;
            }
            foreach (var c in lista.EnumerateArray())
            {
                if (c.TryGetInt32(out int celda)) donde.Add(celda);
            }
        }

        /// <summary>
        /// Un arena al azar con sitio para esa gente por bando, o null si no hay ninguna.
        /// </summary>
        /// <remarks>
        /// Null no es un fallo: significa que no está el fichero, y entonces el combate se monta en
        /// el arena de siempre. Un koliseo en un sitio raro es mejor que un koliseo que no arranca.
        /// </remarks>
        public static Arena? PickFor(int teamSize)
        {
            EnsureLoaded();

            var caben = new List<Arena>();
            foreach (var arena in _arenas) if (arena.Capacity >= teamSize) caben.Add(arena);
            if (caben.Count == 0) return null;

            lock (_azar) return caben[_azar.Next(caben.Count)];
        }
    }
}
