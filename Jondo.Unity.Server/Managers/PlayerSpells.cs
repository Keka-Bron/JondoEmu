using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The spells that belong to the players' side: the classes' own and everything they reach --
    /// the spells they chain, the glyphs they lay, the summons they bring and those summons'
    /// spells. The rest are the monsters'.
    /// </summary>
    /// <remarks>
    /// The two are resolved alike, row by row, but a monster spell's triggered rows are ARMED at
    /// the cast on every fighter their mask and zone name -- the dofuspourlesnoobs guide on Klim:
    /// "Carcassetagne is applied to Klime and every monster: at the start of each of THEIR turns"
    /// -- while the class spells keep the hooks measured on their captures. Drawing the line at
    /// the spell keeps every class capture as it was.
    /// </remarks>
    public static class PlayerSpells
    {
        private static HashSet<int>? _spells;
        private static readonly object _lock = new();

        private static readonly HashSet<int> Chains = new HashSet<int>
        {
            792, 793, 1160, 1017, 1018, 1019, 2160, 2792, 2793, 2794, 2795, 2960,
            400, 401, 402, 1091, 1165, 2022,
        };

        private static readonly HashSet<int> SummonEffects = new HashSet<int> { 181, 1008, 1011, 405 };

        /// <summary>Whether a spell is a class spell or reached from one.</summary>
        public static bool Contains(int spell)
        {
            Load();
            return _spells!.Contains(spell);
        }

        /// <summary>How many spells the players' side holds.</summary>
        public static int Count { get { Load(); return _spells!.Count; } }

        private static void Load()
        {
            if (_spells != null) return;
            lock (_lock)
            {
                if (_spells != null) return;
                var found = new HashSet<int>();
                try
                {
                    using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                    connection.Open();

                    var queue = new Queue<int>();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "SELECT SpellIdsJson FROM SpellVariants;";
                        using var reader = command.ExecuteReader();
                        while (reader.Read())
                        {
                            if (reader.IsDBNull(0)) continue;
                            using var doc = JsonDocument.Parse(reader.GetString(0));
                            foreach (var id in Array(doc.RootElement))
                                if (found.Add(id)) queue.Enqueue(id);
                        }
                    }

                    // Each spell's children, over every grade; each summon's spells.
                    var children = new Dictionary<int, HashSet<int>>();
                    var summons = new Dictionary<int, HashSet<int>>();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "SELECT SpellId, EffectsJson FROM SpellLevels;";
                        using var reader = command.ExecuteReader();
                        while (reader.Read())
                        {
                            int spell = (int)reader.GetInt64(0);
                            if (reader.IsDBNull(1)) continue;
                            using var doc = JsonDocument.Parse(reader.GetString(1));
                            if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                            foreach (var effect in doc.RootElement.EnumerateArray())
                            {
                                int id = Int(effect, "effectId"), dice = Int(effect, "diceNum");
                                if (dice <= 0) continue;
                                if (Chains.Contains(id)) Add(children, spell, dice);
                                else if (SummonEffects.Contains(id)) Add(summons, spell, dice);
                            }
                        }
                    }

                    var levels = new Dictionary<int, int>();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "SELECT Id, SpellId FROM SpellLevels;";
                        using var reader = command.ExecuteReader();
                        while (reader.Read()) levels[(int)reader.GetInt64(0)] = (int)reader.GetInt64(1);
                    }

                    var templateSpells = new Dictionary<int, List<int>>();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "SELECT Id, Data FROM MonsterTemplates;";
                        using var reader = command.ExecuteReader();
                        while (reader.Read())
                        {
                            var list = new List<int>();
                            using var doc = JsonDocument.Parse(reader.GetString(1));
                            if (doc.RootElement.TryGetProperty("spells", out var spells)) list.AddRange(Array(spells));
                            if (doc.RootElement.TryGetProperty("grades", out var grades))
                            {
                                foreach (var grade in ArrayElements(grades))
                                {
                                    int level = Int(grade, "startingSpellId");
                                    if (level > 0 && levels.TryGetValue(level, out int s)) list.Add(s);
                                }
                            }
                            templateSpells[(int)reader.GetInt64(0)] = list;
                        }
                    }

                    while (queue.Count > 0)
                    {
                        int spell = queue.Dequeue();
                        if (children.TryGetValue(spell, out var kids))
                            foreach (int kid in kids) if (found.Add(kid)) queue.Enqueue(kid);
                        if (summons.TryGetValue(spell, out var templates))
                            foreach (int template in templates)
                                if (templateSpells.TryGetValue(template, out var theirs))
                                    foreach (int s in theirs) if (found.Add(s)) queue.Enqueue(s);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Hechizos] Could not work out the players' spells: {ex.Message}");
                }
                _spells = found;
            }
        }

        private static void Add(Dictionary<int, HashSet<int>> map, int key, int value)
        {
            if (!map.TryGetValue(key, out var set)) map[key] = set = new HashSet<int>();
            set.Add(value);
        }

        private static IEnumerable<int> Array(JsonElement element)
        {
            foreach (var e in ArrayElements(element))
                if (e.ValueKind == JsonValueKind.Number) yield return e.GetInt32();
        }

        private static IEnumerable<JsonElement> ArrayElements(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("Array", out var inner)) element = inner;
            if (element.ValueKind != JsonValueKind.Array) yield break;
            foreach (var e in element.EnumerateArray()) yield return e;
        }

        private static int Int(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out int n) ? n : 0;
    }
}
