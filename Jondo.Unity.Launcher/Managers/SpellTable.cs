using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// Qué hechizos tiene un personaje, y cuál de cada pareja.
    ///
    /// Los hechizos no van sueltos: van en PAREJAS de base y variante, y el personaje lleva UNO de
    /// cada pareja, el que haya elegido. Son 22 parejas por raza más 13 comunes —dominio del arma,
    /// zanahowia, las invocaciones de pergamino— que no dependen de la raza.
    ///
    /// Eso se leyó del hms de la captura, que es lo que zanjó el asunto: un sacrogrito de nivel 154
    /// recibía 36 hechizos, no los 44 que tiene apuntados su raza, y los 22 que faltaban eran
    /// exactamente la otra mitad de cada pareja. Mandar las dos mitades es lo que dejaba la barra
    /// de hechizos vacía.
    ///
    ///   spell_variants.json   { breedId, id, spellIds: [base, variante] }
    ///   SpellLevels           una fila por hechizo y grado, con el nivel que pide
    ///   CharacterSpellChoices lo que el jugador ha elegido, que es lo único que no es del cliente
    ///
    /// Una pareja se abre cuando el nivel alcanza el primer grado de alguno de sus dos hechizos.
    /// La variante siempre pide más nivel que la base, así que hasta que no se llega a ella lo que
    /// viaja es la base, se haya elegido lo que se haya elegido.
    /// </summary>
    public static class SpellTable
    {
        /// <summary>La raza que guarda los hechizos comunes, los que no son de ninguna clase.</summary>
        private const int CommonBreed = 19;

        public sealed class Pair
        {
            public int Id { get; init; }
            public int BreedId { get; init; }
            public int Base { get; init; }
            public int Variant { get; init; }

            public bool Holds(int spellId) => spellId == Base || spellId == Variant;
        }

        /// <summary>Las parejas de cada raza, en el orden en que las declara el cliente.</summary>
        private static readonly Dictionary<int, List<Pair>> _pairsByBreed = new Dictionary<int, List<Pair>>();

        /// <summary>Las comunes, que las lleva todo el mundo.</summary>
        private static readonly List<Pair> _common = new List<Pair>();

        /// <summary>spell id -> (grado -> nivel que pide).</summary>
        private static readonly Dictionary<int, SortedDictionary<int, int>> _grades =
            new Dictionary<int, SortedDictionary<int, int>>();

        private static readonly Dictionary<int, Pair> _pairsById = new Dictionary<int, Pair>();

        public static bool IsLoaded => _pairsByBreed.Count > 0;
        public static int PairCount => _pairsById.Count;

        public static void Initialize()
        {
            _pairsByBreed.Clear();
            _common.Clear();
            _grades.Clear();
            _pairsById.Clear();

            LoadGrades();
            LoadPairs();

            Console.WriteLine($"[SpellTable] {_pairsById.Count} parejas de hechizo " +
                              $"({_pairsByBreed.Count} razas y {_common.Count} comunes), " +
                              $"{_grades.Count} hechizos con sus niveles.");
        }

        private static void LoadGrades()
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var levels = connection.CreateCommand();
                levels.CommandText = "SELECT SpellId, Grade, MinPlayerLevel FROM SpellLevels;";
                using var reader = levels.ExecuteReader();
                while (reader.Read())
                {
                    int spell = reader.GetInt32(0);
                    int grade = reader.GetInt32(1);
                    int level = reader.IsDBNull(2) ? 1 : reader.GetInt32(2);

                    if (!_grades.TryGetValue(spell, out var map))
                    {
                        map = new SortedDictionary<int, int>();
                        _grades[spell] = map;
                    }
                    map[grade] = level;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SpellTable] No se pudieron leer los niveles de hechizo: {ex.Message}");
            }
        }

        /// <summary>
        /// Las parejas, de spell_variants.json. Se descartan las que el propio cliente marca con
        /// "[!]" en el nombre, que son las que no están en el juego.
        /// </summary>
        private static void LoadPairs()
        {
            string path = Paths.SpellVariantsJson;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[SpellTable] Falta {Path.GetFileName(path)}: sin él no se sabe " +
                                  "qué hechizos hacen pareja y la barra sale vacía.");
                return;
            }

            var names = SpellNames();
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("references", out var references) ||
                    !references.TryGetProperty("RefIds", out var refIds))
                {
                    Console.WriteLine("[SpellTable] spell_variants.json no tiene el bloque references.");
                    return;
                }

                foreach (var entry in refIds.EnumerateArray())
                {
                    if (!entry.TryGetProperty("data", out var data) ||
                        data.ValueKind != JsonValueKind.Object) continue;
                    if (!data.TryGetProperty("id", out var id) ||
                        !data.TryGetProperty("breedId", out var breed) ||
                        !data.TryGetProperty("spellIds", out var spellIds) ||
                        !spellIds.TryGetProperty("Array", out var array)) continue;

                    var ids = new List<int>();
                    foreach (var value in array.EnumerateArray())
                    {
                        if (value.TryGetInt32(out int spell)) ids.Add(spell);
                    }
                    if (ids.Count != 2) continue;

                    if (Unreleased(names, ids[0]) || Unreleased(names, ids[1])) continue;

                    var pair = new Pair
                    {
                        Id = id.GetInt32(),
                        BreedId = breed.GetInt32(),
                        Base = ids[0],
                        Variant = ids[1],
                    };
                    _pairsById[pair.Id] = pair;

                    if (pair.BreedId == CommonBreed)
                    {
                        _common.Add(pair);
                    }
                    else
                    {
                        if (!_pairsByBreed.TryGetValue(pair.BreedId, out var list))
                        {
                            list = new List<Pair>();
                            _pairsByBreed[pair.BreedId] = list;
                        }
                        list.Add(pair);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SpellTable] No se pudo leer {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        private static bool Unreleased(Dictionary<int, string> names, int spellId)
            => names.TryGetValue(spellId, out string? name) && name.StartsWith("[!]", StringComparison.Ordinal);

        private static Dictionary<int, string> SpellNames()
        {
            var names = new Dictionary<int, string>();
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT s.Id, t.Text FROM Spells s JOIN Translations t ON t.Key = CAST(s.NameId AS TEXT);";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(1)) names[reader.GetInt32(0)] = reader.GetString(1);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SpellTable] No se pudieron leer los nombres de hechizo: {ex.Message}");
            }
            return names;
        }

        /// <summary>Un hechizo que el personaje tiene, con el grado que su nivel abre.</summary>
        public readonly struct KnownSpell
        {
            public KnownSpell(int pairId, int spellId, int grade)
            {
                PairId = pairId; SpellId = spellId; Grade = grade;
            }

            public int PairId { get; }
            public int SpellId { get; }
            public int Grade { get; }
        }

        /// <summary>
        /// Los hechizos del personaje: uno por pareja, al grado más alto que su nivel alcanza.
        ///
        /// Primero las de su raza y después las comunes, que es el orden del hms de la captura.
        /// Una pareja de la que no se alcanza ningún grado no viaja: eso es lo que hace más corto
        /// el panel de un nivel 50 que el de un nivel 200.
        /// </summary>
        public static List<KnownSpell> KnownFor(int breed, int level, IReadOnlyDictionary<int, int>? chosen = null)
        {
            var known = new List<KnownSpell>();

            _pairsByBreed.TryGetValue(breed, out var own);
            foreach (var pair in own ?? new List<Pair>()) Add(known, pair, level, chosen);
            foreach (var pair in _common) Add(known, pair, level, chosen);

            return known;
        }

        private static void Add(List<KnownSpell> into, Pair pair, int level, IReadOnlyDictionary<int, int>? chosen)
        {
            // Lo elegido manda, y si no hay nada elegido va la base. Si el nivel todavía no llega a
            // la elegida —la variante siempre pide más— viaja la otra: la pareja está abierta y el
            // personaje tiene que poder lanzar algo de ella.
            int wanted = pair.Base;
            if (chosen != null && chosen.TryGetValue(pair.Id, out int picked) && pair.Holds(picked))
            {
                wanted = picked;
            }

            int grade = HighestGrade(wanted, level);
            if (grade == 0)
            {
                wanted = wanted == pair.Base ? pair.Variant : pair.Base;
                grade = HighestGrade(wanted, level);
            }
            if (grade > 0) into.Add(new KnownSpell(pair.Id, wanted, grade));
        }

        /// <summary>El grado de este hechizo que abre este nivel, o 0 si no abre ninguno.</summary>
        public static int GradeFor(int spellId, int level) => HighestGrade(spellId, level);

        private static int HighestGrade(int spellId, int level)
        {
            if (!_grades.TryGetValue(spellId, out var grades)) return 0;

            int best = 0;
            foreach (var pair in grades)
            {
                if (pair.Value <= level && pair.Key > best) best = pair.Key;
            }
            return best;
        }

        /// <summary>La pareja a la que pertenece un hechizo, o null si no es de ninguna.</summary>
        public static Pair? PairOf(int spellId)
        {
            foreach (var pair in _pairsById.Values)
            {
                if (pair.Holds(spellId)) return pair;
            }
            return null;
        }
    }
}
