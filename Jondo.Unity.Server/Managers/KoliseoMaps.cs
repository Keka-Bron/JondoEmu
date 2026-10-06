using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The koliseo arenas, with their placement cells per side.
    /// </summary>
    /// <remarks>
    /// A koliseo is not fought in the arena the roleplay map would get: it is fought in one of the
    /// koliseo arenas, and the game picks one at random. They are <b>441 map identifiers</b> in
    /// three subareas, counted over <c>MapSubareas</c>:
    ///
    /// <code>
    ///    885  Koliseo - Duelo            85 maps
    ///   1122  Koliseo - Equipos          88
    ///   1123  Koliseo - Entrenamiento   268
    /// </code>
    ///
    /// The subarea's name is a numeric identifier that has to be resolved against
    /// <c>Translations</c>; that is why searching the text «koliseo» in the subarea table finds
    /// nothing. And they are not 441 different arenas: by placement cells they come down to <b>101 designs</b>,
    /// most with five copies.
    ///
    /// <b>SIZE MATTERS, and that is why it is not picked by subarea.</b> The Duelo ones are really
    /// small —37 of 85 have only one cell per side and 77 of 85 do not fit three— while
    /// the Equipos ones never go below four. Picking «the subarea it gets» would put a 3 versus 3
    /// on a map with room for one. It is picked by CAPACITY, which is the minimum of the two lists, and
    /// then the one versus one falls on its own on the small ones and the three versus three on the large ones.
    ///
    /// The cells come from the client itself, from the <c>red</c> and <c>blue</c> flags of
    /// <c>cellsData[]</c>, and are checked against the real server's kba on map
    /// 233308168 of the 2 versus 2 capture: the same two sets.
    ///
    /// <b>Mind the names, they cross over.</b> The kba sends team 0 in its f1, and that f1 is the
    /// list the client calls <c>red</c>. So the client's red ones are our BLUE team.
    /// The file keeps the client's names and the translation is done here, once.
    /// </remarks>
    public static class KoliseoMaps
    {
        public sealed class Arena
        {
            public long MapId { get; init; }
            public int SubAreaId { get; init; }
            public string Name { get; init; } = "";

            /// <summary>The blue team's. They are the ones the client calls red.</summary>
            public List<int> Blue { get; init; } = new List<int>();

            /// <summary>The red team's. The ones the client calls blue.</summary>
            public List<int> Red { get; init; } = new List<int>();

            /// <summary>How many people fit per side: what decides which mode it serves.</summary>
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

        /// <summary>How many arenas fit that many people per side.</summary>
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

                    // The client's red ones are our blue, and the other way round. See the comment above.
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
        /// A random arena with room for that many people per side, or null if there is none.
        /// </summary>
        /// <remarks>
        /// Null is not a failure: it means the file is not there, and then the fight is set up in
        /// the usual arena. A koliseo in an odd place is better than a koliseo that does not start.
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
