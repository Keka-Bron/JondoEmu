using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What a raid treasure is worth, read from the items themselves.
    /// </summary>
    /// <remarks>
    /// There is no list of treasures anywhere in here, and there should not be one. Effect 4063 is
    /// called, in the client's own effect table, "Valor de un objeto", and ELEVEN items in the whole
    /// game carry it -- the seven gems of the Abyss from Quartz at 2 to Ónix at 30, the three
    /// guardians' trophies at 1000, 5000 and 10000, and the depths salt at 1. All eleven are of
    /// item type 315, "Recurso de raid". So the question "is this a treasure, and for how much" has
    /// one answer and the data gives it: whatever carries a value.
    ///
    /// That the salt is worth one is not a rounding error, it is a decision the player gets to
    /// make: the same handful either buys a band of light or goes in the chest.
    /// </remarks>
    public static class RaidTreasures
    {
        /// <summary>"Valor de un objeto", the effect that makes something a treasure.</summary>
        public const int ValueEffect = 4063;

        private static Dictionary<int, int>? _values;
        private static readonly object _lock = new();

        /// <summary>Every treasure in the game and what it scores, by item template.</summary>
        public static IReadOnlyDictionary<int, int> Values
        {
            get
            {
                Ensure();
                return _values!;
            }
        }

        /// <summary>What one of them scores, or zero when it is not a treasure at all.</summary>
        public static int ValueOf(int gid)
        {
            Ensure();
            return _values!.TryGetValue(gid, out int worth) ? worth : 0;
        }

        /// <summary>The treasures somebody is carrying right now, and how many of each.</summary>
        public static Dictionary<int, int> InTheBag()
        {
            var carried = new Dictionary<int, int>();
            foreach (int gid in Values.Keys)
            {
                int many = Equipment.HowMany(gid);
                if (many > 0) carried[gid] = many;
            }

            return carried;
        }

        /// <summary>What a bagful of them adds up to.</summary>
        public static long Worth(IReadOnlyDictionary<int, int> carried)
        {
            long total = 0;
            foreach (var kv in carried) total += (long)ValueOf(kv.Key) * kv.Value;
            return total;
        }

        /// <summary>For the tests: read them again.</summary>
        internal static void Forget()
        {
            lock (_lock) _values = null;
        }

        private static void Ensure()
        {
            lock (_lock)
            {
                if (_values != null) return;
                _values = Read();
            }
        }

        /// <summary>
        /// The eleven, out of the base.
        /// </summary>
        /// <remarks>
        /// Two queries and no scan of the 21,748 templates: the effect table gives the handful of
        /// rows that carry a value, and then the templates are asked which of them owns each row.
        /// A template's effects live inside its JSON as a list of rids, so the second question is a
        /// LIKE on that text -- ugly to read, but it is one pass in C over a column instead of
        /// twenty-one thousand JSON parses in ours.
        /// </remarks>
        private static Dictionary<int, int> Read()
        {
            var found = new Dictionary<int, int>();
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var worth = new Dictionary<long, int>();
                var effects = connection.CreateCommand();
                effects.CommandText = "SELECT Rid, Value FROM ItemEffects WHERE EffectId = $effect;";
                effects.Parameters.AddWithValue("$effect", ValueEffect);
                using (var reader = effects.ExecuteReader())
                {
                    while (reader.Read()) worth[reader.GetInt64(0)] = reader.GetInt32(1);
                }

                if (worth.Count == 0) return found;

                var wanted = connection.CreateCommand();
                var conditions = new List<string>();
                int at = 0;
                foreach (long rid in worth.Keys)
                {
                    string name = "$r" + at++;
                    conditions.Add("Data LIKE " + name);
                    wanted.Parameters.AddWithValue(name, "%" + rid + "%");
                }

                wanted.CommandText = "SELECT Id, Data FROM ItemTemplates WHERE " +
                                     string.Join(" OR ", conditions) + ";";

                using var templates = wanted.ExecuteReader();
                while (templates.Read())
                {
                    int gid = templates.GetInt32(0);
                    string data = templates.IsDBNull(1) ? "" : templates.GetString(1);
                    if (data.Length == 0) continue;

                    using var doc = System.Text.Json.JsonDocument.Parse(data);
                    if (!doc.RootElement.TryGetProperty("possibleEffects", out var list)) continue;
                    if (!list.TryGetProperty("Array", out var array)) continue;

                    foreach (var entry in array.EnumerateArray())
                    {
                        if (!entry.TryGetProperty("rid", out var rid)) continue;
                        if (!worth.TryGetValue(rid.GetInt64(), out int value)) continue;
                        if (value > 0) found[gid] = value;
                        break;
                    }
                }

                Console.WriteLine($"[Raids] {found.Count} tesoros con valor propio.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Raids] No se han podido leer los valores de los tesoros: {ex.Message}");
            }

            return found;
        }
    }
}
