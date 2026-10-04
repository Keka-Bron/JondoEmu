using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The kanojedo: the training hall, its punching bags and the master that sets a session up.
    ///
    /// Measured on the Amakna village kanojedo, map 99090957 "The Kanojedo", in the class
    /// captures: the master is NPC 7416, a Puch Ingball drawn at 125 %, and its conversation is
    /// two screens.
    ///
    ///     ...                                          (54965)
    ///       · Adaptar la sesión de entrenamiento al nivel 200.     73807
    ///       · ... al nivel 100.                                    73813
    ///       · ... al nivel 75.                                     73819
    ///       · ... al nivel 50.                                     73825
    ///       · ... al nivel 25.                                     73831
    ///       · ... al nivel 1.                                      73837
    ///
    ///     ...                                          (54966 + the level's index)
    ///       · Entrenarte con 1 puch ingball.                       the level's reply - 5
    ///       · Entrenarte con 2 puchs ingball.                                       - 4
    ///       · Entrenarte con 3 puchs ingball.                                       - 3
    ///       · Entrenarte con 4 puchs ingball.                                       - 2
    ///       · Ir a otro nivel de entrenamiento.                                     - 1
    ///
    /// Pick a count and the fight opens on the spot: kld, then the same burst as walking into a
    /// group, with a group id nobody had seen on the map. The level 50 and level 25 branches are
    /// the ones actually walked in the captures (54969 with 73820-73824, 54970 with 73826-73830);
    /// the other four follow the same arithmetic and their reply texts, which are the client's,
    /// say the same words.
    /// </summary>
    /// <remarks>
    /// WHAT A PUCH IS, and why the fight is safe, is in the data and not here: every puch has 0
    /// action points and -1 movement points at every grade, so the monster's turn casts nothing
    /// and walks nowhere. Give one a point of either and it will do exactly what the data says.
    ///
    /// Which puchs turn up is this server's: the real master sends Puch Ingball only, and here the
    /// themed ones -- Vil Smis, Sombra, Hiperescampo, Sylargh, Cráneo Rosa -- take turns with it,
    /// picked at random among those that have a grade at the level asked for. At 200 that is the
    /// Ingball alone, because it is the only one with a sixth grade.
    /// </remarks>
    public static class Kanojedo
    {
        /// <summary>The master, NPC 7416 "Puch Ingball".</summary>
        public const int MasterNpc = 7416;

        /// <summary>The race every puch belongs to, "Puchs" in the client's catalogue.</summary>
        public const int PuchRace = 250;

        /// <summary>The first screen, and the one the master's bubble shows.</summary>
        public const long FirstMessage = 54965;

        /// <summary>Up to four at a time, which is what the master offers.</summary>
        public const int MostPuchs = 4;

        /// <summary>
        /// A number every "Entrenarte con N" reply carries as its parameter, and none of the
        /// others does. Measured in every capture of the conversation; what it means is not.
        /// </summary>
        public const long ReplyParameter = 905;

        /// <summary>The six levels in the order the master lists them, each with its reply.</summary>
        private static readonly (int Level, long Reply)[] Levels =
        {
            (200, 73807), (100, 73813), (75, 73819), (50, 73825), (25, 73831), (1, 73837),
        };

        /// <summary>The replies of the first screen, in the master's order.</summary>
        public static IReadOnlyList<long> LevelReplies => Levels.Select(l => l.Reply).ToList();

        /// <summary>Which of the six a reply picks, from zero, or -1 when it is none of them.</summary>
        public static int LevelIndexOf(long reply)
        {
            for (int i = 0; i < Levels.Length; i++)
            {
                if (Levels[i].Reply == reply) return i;
            }

            return -1;
        }

        /// <summary>The level of one of the six.</summary>
        public static int LevelAt(int index)
            => index >= 0 && index < Levels.Length ? Levels[index].Level : 0;

        /// <summary>The second screen's message for one of the six levels.</summary>
        public static long MessageFor(int levelIndex) => FirstMessage + 1 + levelIndex;

        /// <summary>The second screen's replies: one to four puchs, and back to the levels.</summary>
        public static IReadOnlyList<long> CountReplies(int levelIndex)
        {
            // The block sits right under the level's reply: for level 200 (73807) it is 73802 to
            // 73806, one puch first and "Ir a otro nivel" last.
            long levelReply = Levels[levelIndex].Reply;
            long first = levelReply - 1 - MostPuchs;
            var replies = new List<long>();
            for (int count = 1; count <= MostPuchs; count++) replies.Add(first + count - 1);
            replies.Add(levelReply - 1);
            return replies;
        }

        /// <summary>Whether a "count" reply is the one that goes back to the level list.</summary>
        public static bool IsBack(long reply)
        {
            foreach (var (_, levelReply) in Levels)
            {
                if (reply == levelReply - 1) return true;
            }

            return false;
        }

        /// <summary>
        /// What a second-screen reply asked for: the level's index and how many puchs, or null
        /// when the reply is not one of those.
        /// </summary>
        public static (int LevelIndex, int Count)? ReadCount(long reply)
        {
            for (int i = 0; i < Levels.Length; i++)
            {
                long first = Levels[i].Reply - 1 - MostPuchs;
                if (reply >= first && reply < first + MostPuchs) return (i, (int)(reply - first) + 1);
            }

            return null;
        }

        /// <summary>Whether a reply is the master's at all.</summary>
        public static bool Owns(long reply)
            => LevelIndexOf(reply) >= 0 || IsBack(reply) || ReadCount(reply) != null;

        /// <summary>Whether this map is a kanojedo: the master stands on it.</summary>
        public static bool IsDojo(long mapId)
        {
            foreach (var spawn in Npcs.Of(mapId))
            {
                if (spawn.NpcId == MasterNpc) return true;
            }

            return false;
        }

        // ─── The puchs ──────────────────────────────────────────────────────────

        /// <summary>One puch the master may send: which monster, and which grade sits at which level.</summary>
        public sealed class Puch
        {
            public int MonsterId { get; init; }
            public string Name { get; init; } = "";

            /// <summary>Grade index by level, as the monster's own grade table says.</summary>
            public IReadOnlyDictionary<int, int> GradeAtLevel { get; init; } = new Dictionary<int, int>();
        }

        private static List<Puch>? _puchs;
        private static readonly object _lock = new();
        private static readonly Random _dice = new();

        /// <summary>Every puch the client has a name for.</summary>
        public static IReadOnlyList<Puch> Puchs
        {
            get
            {
                Ensure();
                return _puchs!;
            }
        }

        /// <summary>The puchs that have a grade at a level, with that grade.</summary>
        public static List<(int Monster, int Grade)> PoolAt(int level)
        {
            var pool = new List<(int, int)>();
            foreach (var puch in Puchs)
            {
                if (puch.GradeAtLevel.TryGetValue(level, out int grade)) pool.Add((puch.MonsterId, grade));
            }

            return pool;
        }

        /// <summary>
        /// A training session: so many puchs at that level, drawn at random from the pool, with
        /// repeats allowed -- four at level 200 are four Ingballs, there being nothing else.
        /// </summary>
        public static List<(int Monster, int Grade)> Pick(int level, int count, Random? dice = null)
        {
            var pool = PoolAt(level);
            var picked = new List<(int, int)>();
            if (pool.Count == 0) return picked;

            dice ??= _dice;
            for (int i = 0; i < Math.Clamp(count, 1, MostPuchs); i++)
            {
                int at;
                lock (dice) at = dice.Next(pool.Count);
                picked.Add(pool[at]);
            }

            return picked;
        }

        /// <summary>For the tests: read them again.</summary>
        internal static void Forget()
        {
            lock (_lock) _puchs = null;
        }

        private static void Ensure()
        {
            lock (_lock)
            {
                if (_puchs != null) return;
                _puchs = Read();
            }
        }

        /// <summary>
        /// The puchs, out of the base: race 250 and a name the client can show.
        /// </summary>
        /// <remarks>
        /// Nine monsters carry the race. Three of them have no Spanish name -- the catalogue
        /// marks them <c>[!]</c> and two of those, the "Poutch d'expérimentation", carry a dozen
        /// real spells -- and a monster the client cannot name is not one to put in front of a
        /// player. That leaves the Ingball and the five themed ones, all with the same single
        /// spell and no points to cast it with.
        /// </remarks>
        private static List<Puch> Read()
        {
            var found = new List<Puch>();
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var query = connection.CreateCommand();
                query.CommandText = "SELECT m.Id, t.Text, m.Data FROM MonsterTemplates m " +
                                    "LEFT JOIN Translations t ON t.Key = CAST(m.NameId AS TEXT) " +
                                    "WHERE m.Data LIKE $race ORDER BY m.Id;";
                query.Parameters.AddWithValue("$race", "%\"race\": " + PuchRace + "%");

                using var reader = query.ExecuteReader();
                while (reader.Read())
                {
                    int id = reader.GetInt32(0);
                    string name = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    if (name.Length == 0 || name.StartsWith("[!]", StringComparison.Ordinal)) continue;

                    using var doc = JsonDocument.Parse(reader.GetString(2));
                    if (!doc.RootElement.TryGetProperty("race", out var race) || race.GetInt32() != PuchRace) continue;
                    if (!doc.RootElement.TryGetProperty("grades", out var grades)) continue;
                    if (!grades.TryGetProperty("Array", out var list)) continue;

                    var byLevel = new Dictionary<int, int>();
                    int index = 0;
                    foreach (var grade in list.EnumerateArray())
                    {
                        if (grade.TryGetProperty("level", out var level) && index < MobSpawnManager.MobMember.MaxWrittenGrades)
                        {
                            byLevel.TryAdd(level.GetInt32(), index);
                        }

                        index++;
                    }

                    found.Add(new Puch { MonsterId = id, Name = name, GradeAtLevel = byLevel });
                }

                Console.WriteLine($"[Kanojedo] {found.Count} puchs con nombre: " +
                                  string.Join(", ", found.Select(p => p.Name)) + ".");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Kanojedo] No se han podido leer los puchs: {ex.Message}");
            }

            return found;
        }
    }
}
