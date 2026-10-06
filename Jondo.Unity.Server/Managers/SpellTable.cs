using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Which spells a character has, and which one of each pair.
    ///
    /// Spells do not go loose: they go in PAIRS of base and variant, and the character carries ONE of
    /// each pair, the one he has chosen. There are 22 pairs per breed plus 13 common ones —weapon mastery,
    /// zanahowia, the scroll summons— that do not depend on the breed.
    ///
    /// That was read from the capture's hms, which is what settled the matter: a level 154 sacrier
    /// received 36 spells, not the 44 his breed has recorded, and the 22 missing ones were
    /// exactly the other half of each pair. Sending both halves is what left the spell bar
    /// empty.
    ///
    ///   spell_variants.json   { breedId, id, spellIds: [base, variant] }
    ///   SpellLevels           one row per spell and grade, with the level it asks for
    ///   CharacterSpellChoices what the player has chosen, which is the only thing not from the client
    ///
    /// A pair unlocks when the level reaches the first grade of either of its two spells.
    /// The variant always asks for more level than the base, so until it is reached what
    /// travels is the base, whatever has been chosen.
    /// </summary>
    public static class SpellTable
    {
        /// <summary>The breed that holds the common spells, the ones that belong to no class.</summary>
        private const int CommonBreed = 19;

        public sealed class Pair
        {
            public int Id { get; init; }
            public int BreedId { get; init; }
            public int Base { get; init; }
            public int Variant { get; init; }

            public bool Holds(int spellId) => spellId == Base || spellId == Variant;
        }

        /// <summary>Each breed's pairs, in the order the client declares them.</summary>
        private static readonly Dictionary<int, List<Pair>> _pairsByBreed = new Dictionary<int, List<Pair>>();

        /// <summary>The common ones, which everyone carries.</summary>
        private static readonly List<Pair> _common = new List<Pair>();

        /// <summary>spell id -> (grado -> nivel que pide).</summary>
        private static readonly Dictionary<int, SortedDictionary<int, int>> _grades =
            new Dictionary<int, SortedDictionary<int, int>>();

        private static readonly Dictionary<int, Pair> _pairsById = new Dictionary<int, Pair>();

        /// <summary>
        /// Whether the tables are read in and safe to use. Volatile because the fast path in
        /// <see cref="Ensure"/> reads it outside the lock, and raised LAST so a reader never
        /// catches the four collections part-built.
        /// </summary>
        private static volatile bool _loaded;
        private static readonly object _lock = new object();

        public static bool IsLoaded { get { Ensure(); return _pairsByBreed.Count > 0; } }
        public static int PairCount { get { Ensure(); return _pairsById.Count; } }

        /// <summary>
        /// Reads the spell tables, once per run. Kept as a separate call so the server pays for
        /// it at boot, with its log line, and not on the first spell list somebody opens.
        /// </summary>
        public static void Initialize() => Ensure();

        private static void Ensure()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                try
                {
                    Load();
                }
                finally
                {
                    _loaded = true;   // in a finally so a missing file counts as tried
                }
            }
        }

        private static void Load()
        {
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
        /// The pairs, from spell_variants.json. The ones the client itself marks with
        /// "[!]" in the name are discarded, which are the ones not in the game.
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

        /// <summary>A spell the character has, with the grade his level unlocks.</summary>
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
        /// The character's spells: one per pair, at the highest grade his level reaches.
        ///
        /// First those of his breed and then the common ones, which is the order of the capture's hms.
        /// A pair of which no grade is reached does not travel: that is what makes a level 50's panel
        /// shorter than a level 200's.
        /// </summary>
        public static List<KnownSpell> KnownFor(int breed, int level, IReadOnlyDictionary<int, int>? chosen = null)
        {
            Ensure();
            var known = new List<KnownSpell>();

            _pairsByBreed.TryGetValue(breed, out var own);
            foreach (var pair in own ?? new List<Pair>()) Add(known, pair, level, chosen);
            foreach (var pair in _common) Add(known, pair, level, chosen);

            return known;
        }

        private static void Add(List<KnownSpell> into, Pair pair, int level, IReadOnlyDictionary<int, int>? chosen)
        {
            // What is chosen rules, and if nothing is chosen the base goes. If the level does not yet reach
            // the chosen one —the variant always asks for more— the other one travels: the pair is open and the
            // character has to be able to cast something from it.
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

        /// <summary>The classes with spells of their own: every breed but the common spells' one.</summary>
        public static IReadOnlyList<int> ClassBreeds
        {
            get
            {
                Ensure();
                return _pairsByBreed.Keys.Where(b => b != CommonBreed).OrderBy(b => b).ToList();
            }
        }

        /// <summary>A class's pairs of spells, base and variant, in the client's order.</summary>
        public static IReadOnlyList<Pair> PairsOf(int breed)
        {
            Ensure();
            return _pairsByBreed.TryGetValue(breed, out var pairs) ? pairs : new List<Pair>();
        }

        /// <summary>The grade of this spell this level unlocks, or 0 if it unlocks none.</summary>
        public static int GradeFor(int spellId, int level) => HighestGrade(spellId, level);

        private static int HighestGrade(int spellId, int level)
        {
            Ensure();
            if (!_grades.TryGetValue(spellId, out var grades)) return 0;

            int best = 0;
            foreach (var pair in grades)
            {
                if (pair.Value <= level && pair.Key > best) best = pair.Key;
            }
            return best;
        }

        /// <summary>The pair a spell belongs to, or null if it belongs to none.</summary>
        public static Pair? PairOf(int spellId)
        {
            Ensure();
            foreach (var pair in _pairsById.Values)
            {
                if (pair.Holds(spellId)) return pair;
            }
            return null;
        }
    }
}
