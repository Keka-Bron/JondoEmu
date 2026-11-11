using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            public int Id;
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

        /// <summary>
        /// Whether the sets are read in and safe to use. Volatile because the fast path in
        /// <see cref="Ensure"/> reads it outside the lock, and raised LAST.
        /// </summary>
        private static volatile bool _loaded;
        private static readonly object _lock = new object();

        /// <summary>
        /// Reads the file, once per run. Kept as a separate call so the server pays for it at
        /// boot, with its log line, rather than on the first sheet somebody opens.
        /// </summary>
        /// <remarks>
        /// Calling it again does nothing, on purpose: item_sets.json comes out of the client and
        /// does not change while the server is up.
        /// </remarks>
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
                    var set = new Set { Id = setId };

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

        /// <summary>Every set's id.</summary>
        public static IReadOnlyList<int> Ids { get { Ensure(); return _byId.Keys.ToList(); } }

        /// <summary>Returns a copy of the templates in a set, for the administration command.</summary>
        public static bool TryGetItems(int setId, out IReadOnlyList<int> items)
        {
            Ensure();
            if (_byId.TryGetValue(setId, out var set))
            {
                items = set.Items.ToArray();
                return true;
            }

            items = Array.Empty<int>();
            return false;
        }

        /// <summary>The sets an item belongs to.</summary>
        public static IReadOnlyList<int> SetsOf(int template)
        {
            Ensure();
            return _byItem.TryGetValue(template, out var sets) ? sets.Select(set => set.Id).ToList() : new List<int>();
        }

        /// <summary>
        /// One set as it stands on a character wearing these items: its pieces worn, and the bonus
        /// for exactly that many (none under two) -- what the set update (iwd) carries.
        /// </summary>
        public static (IReadOnlyList<int> Worn, IReadOnlyList<(int Effect, long Value)> Bonus) Standing(int setId, IEnumerable<int> wornTemplates)
        {
            Ensure();
            if (!_byId.TryGetValue(setId, out var set))
                return (new List<int>(), new List<(int, long)>());
            var worn = wornTemplates.Where(set.Items.Contains).Distinct().ToList();
            var bonus = worn.Count >= 2 && set.Bonuses.TryGetValue(worn.Count, out var effects)
                ? effects : new List<(int, long)>();
            return (worn, bonus);
        }

        public sealed record EquippedSet(int Id, IReadOnlyList<int> Templates,
            IReadOnlyList<(int Effect, long Value)> Effects);

        /// <summary>
        /// Detached, per-set equipment snapshots. One piece still identifies a set, with no
        /// bonus; two or more use the exact piece-count tier, never cumulative tiers.
        /// </summary>
        public static IReadOnlyList<EquippedSet> Equipped(IEnumerable<int> wornTemplates)
        {
            Ensure();
            var pieces = new Dictionary<Set, List<int>>();
            foreach (int template in wornTemplates)
            {
                if (!_byItem.TryGetValue(template, out var sets)) continue;
                foreach (var set in sets)
                {
                    if (!pieces.TryGetValue(set, out var templates)) pieces[set] = templates = new();
                    templates.Add(template);
                }
            }

            return pieces.OrderBy(pair => pair.Key.Id).Select(pair => new EquippedSet(pair.Key.Id,
                pair.Value.ToArray(), pair.Value.Count >= 2 &&
                    pair.Key.Bonuses.TryGetValue(pair.Value.Count, out var effects)
                        ? effects.ToArray() : Array.Empty<(int, long)>())).ToArray();
        }

        /// <summary>
        /// Everything the sets give for this lot of worn items. A set only counts once however
        /// many of its pieces are on, and the bonus is the one for exactly that many — not the sum
        /// of every step below it.
        /// </summary>
        public static List<(int Effect, long Value)> BonusesFor(IEnumerable<int> wornTemplates)
        {
            Ensure();
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
