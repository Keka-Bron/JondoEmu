using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The sets, and what wearing several pieces of one is worth.
    ///
    /// A set gives nothing for one piece and more with each one after that, so the bonus is looked
    /// up by how many are on at once. From the client's own data through extract_item_sets.py.
    ///
    /// This is what the character sheet was missing once each item's own effects were being added
    /// up: the numbers came out short by a fixed amount everywhere — the captured account's
    /// strength read 575 where the real server sends 745 — and the difference was the sets.
    /// </summary>
    public static class ItemSets
    {
        private sealed class Set
        {
            public readonly List<int> Items = new List<int>();
            /// <summary>how many pieces -> the effects that gives.</summary>
            public readonly Dictionary<int, List<(int Effect, long Value)>> Bonuses =
                new Dictionary<int, List<(int, long)>>();
        }

        private static readonly List<Set> _sets = new List<Set>();

        /// <summary>set id -> every item declared by the client, including sets without bonuses.</summary>
        private static readonly Dictionary<int, Set> _byId = new Dictionary<int, Set>();

        /// <summary>item template -> which sets it belongs to.</summary>
        private static readonly Dictionary<int, List<Set>> _byItem = new Dictionary<int, List<Set>>();

        public static int Count => _sets.Count;

        public static void Initialize()
        {
            _sets.Clear();
            _byItem.Clear();
            _byId.Clear();

            string path = Paths.ItemSetsJson;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[ItemSets] {Path.GetFileName(path)} is not there; the sheet will " +
                                  "be short by whatever the sets give. Run extract_item_sets.py.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out int setId)) continue;
                    var set = new Set();

                    if (entry.Value.TryGetProperty("items", out var items))
                    {
                        foreach (var item in items.EnumerateArray())
                        {
                            if (item.TryGetInt32(out int template)) set.Items.Add(template);
                        }
                    }

                    if (entry.Value.TryGetProperty("bonuses", out var bonuses))
                    {
                        foreach (var howMany in bonuses.EnumerateObject())
                        {
                            if (!int.TryParse(howMany.Name, out int pieces)) continue;

                            var effects = new List<(int, long)>();
                            foreach (var pair in howMany.Value.EnumerateArray())
                            {
                                var it = pair.EnumerateArray();
                                if (!it.MoveNext()) continue;
                                int effect = it.Current.GetInt32();
                                if (!it.MoveNext()) continue;
                                effects.Add((effect, it.Current.GetInt64()));
                            }
                            if (effects.Count > 0) set.Bonuses[pieces] = effects;
                        }
                    }

                    // The administration command needs the complete catalogue. Some cosmetic or
                    // internal sets legitimately have no bonus table, but their item list is still
                    // a real set and must not disappear from .itemset.
                    if (set.Items.Count == 0) continue;

                    _byId[setId] = set;
                    _sets.Add(set);
                    foreach (int template in set.Items)
                    {
                        if (!_byItem.TryGetValue(template, out var list))
                        {
                            list = new List<Set>();
                            _byItem[template] = list;
                        }
                        list.Add(set);
                    }
                }

                Console.WriteLine($"[ItemSets] {_sets.Count} sets, over {_byItem.Count} items.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ItemSets] Could not read {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        /// <summary>Returns a copy of the templates in a set, for the administration command.</summary>
        public static bool TryGetItems(int setId, out IReadOnlyList<int> items)
        {
            if (_byId.TryGetValue(setId, out var set))
            {
                items = set.Items.ToArray();
                return true;
            }

            items = Array.Empty<int>();
            return false;
        }

        /// <summary>
        /// Everything the sets give for this lot of worn items. A set only counts once however
        /// many of its pieces are on, and the bonus is the one for exactly that many — not the sum
        /// of every step below it.
        /// </summary>
        public static List<(int Effect, long Value)> BonusesFor(IEnumerable<int> wornTemplates)
        {
            var result = new List<(int, long)>();
            if (_sets.Count == 0) return result;

            var pieces = new Dictionary<Set, int>();
            foreach (int template in wornTemplates)
            {
                if (!_byItem.TryGetValue(template, out var sets)) continue;
                foreach (var set in sets)
                {
                    pieces.TryGetValue(set, out int had);
                    pieces[set] = had + 1;
                }
            }

            foreach (var pair in pieces)
            {
                if (pair.Value < 2) continue;
                if (pair.Key.Bonuses.TryGetValue(pair.Value, out var effects)) result.AddRange(effects);
            }
            return result;
        }
    }
}
