using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Random loot, one of the server's settings (off by default): besides its own table, every
    /// monster beaten has a chance of dropping a handful of two to five items picked at random
    /// around its level, equipment before consumables, by rarity.
    /// </summary>
    /// <remarks>
    /// Each item of the handful is one roll on the rarity table, rarest first:
    ///
    ///   0.05 %  a perfect piece of equipment, every line at its best, with an exo AP or MP
    ///   0.5 %   legendary equipment (the client's IsLegendary flag: 25 pieces, all level 200)
    ///   5 %     a dofus
    ///   10 %    a mount or a pet
    ///   15 %    a cosmetic
    ///   25 %    ordinary equipment
    ///   the rest, 44.45 %, a consumable
    ///
    /// so 55.55 % of what drops is something to wear. The levels: the item within ten levels of
    /// the monster; a dofus within twenty, because they come in steps (100, 160, 180...). Mounts,
    /// pets and cosmetics do not grow with level (pets are 1 or 20, mounts 60, most cosmetics 1),
    /// so any of them up to ten levels above the monster. A rarity with nothing at that level drops
    /// ordinary equipment instead, and failing that, a consumable.
    ///
    /// The families are the client's item types (datos/item_types_3.6.10.11.json): super types 1
    /// to 5, 7, 10 and 11 are equipment, 6 consumables, 12 mounts and pets, category 5 cosmetics,
    /// and type 23 the dofus. A consumable is one the client lets you use and sell, which leaves out
    /// quest papers, obsolete dream items and full soul stones; equipment leaves out the ethereal
    /// weapons, which break.
    /// </remarks>
    public static class RandomLoot
    {
        public enum Rarity { Consumable, Equipment, Cosmetic, Creature, Dofus, Legendary, Perfect }

        /// <summary>The chance of each rarity per item, in percent, rarest first; the rest is a consumable.</summary>
        public static readonly IReadOnlyList<(Rarity Rarity, double Percent)> Odds = new[]
        {
            (Rarity.Perfect, 0.05),
            (Rarity.Legendary, 0.5),
            (Rarity.Dofus, 5.0),
            (Rarity.Creature, 10.0),
            (Rarity.Cosmetic, 15.0),
            (Rarity.Equipment, 25.0),
        };

        public const int FewestItems = 2;
        public const int MostItems = 5;
        public const int LevelWindow = 10;
        public const int DofusLevelWindow = 20;

        private static readonly HashSet<int> EquipmentFamilies = new() { 1, 2, 3, 4, 5, 7, 10, 11 };
        private const int ConsumableFamily = 6;
        private const int CreatureFamily = 12;
        private const int DofusFamily = 13;
        private const int CosmeticCategory = 5;
        private const int DofusType = 23;

        // The client's ItemFlags (Core.DataCenter.Metadata.Item.ItemFlags).
        private const int Usable = 1;
        private const int Etheral = 16;
        private const int Saleable = 1024;
        private const int Legendary = 2048;

        /// <summary>An item that can drop: its id, its level and its rarity.</summary>
        public readonly record struct Candidate(int Gid, int Level, Rarity Rarity);

        /// <summary>An item dropped; a perfect one carries its exo, AP (111) or MP (128).</summary>
        public readonly record struct Drop(int Gid, Rarity Rarity, int Exo = 0);

        private static readonly object _lock = new();
        private static Dictionary<int, (int Family, int Category)> _types;
        private static Dictionary<Rarity, List<Candidate>> _pools;
        private static int _highest;

        // ─── The data ───────────────────────────────────────────────────────────────────────

        /// <summary>The family (super type) and category of every item type, from the client's data.</summary>
        private static Dictionary<int, (int Family, int Category)> Types
        {
            get
            {
                if (_types != null) return _types;
                lock (_lock) return _types ??= ReadTypes();
            }
        }

        private static Dictionary<int, (int Family, int Category)> ReadTypes()
        {
            var types = new Dictionary<int, (int, int)>();
            string path = Paths.ItemTypesJson;
            try
            {
                if (!File.Exists(path))
                {
                    Console.WriteLine($"[RandomLoot] {Path.GetFileName(path)} is missing; no item has a family.");
                    return types;
                }
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var t in doc.RootElement.GetProperty("types").EnumerateArray())
                    types[t.GetProperty("id").GetInt32()] = (t.GetProperty("superTypeId").GetInt32(),
                                                             t.GetProperty("categoryId").GetInt32());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RandomLoot] Could not read {Path.GetFileName(path)}: {ex.Message}");
            }
            return types;
        }

        /// <summary>The items that can drop, by rarity, read once.</summary>
        private static Dictionary<Rarity, List<Candidate>> Pools
        {
            get
            {
                if (_pools != null) return _pools;
                lock (_lock)
                {
                    if (_pools == null) Use(ReadCandidates());
                    return _pools;
                }
            }
        }

        private static List<Candidate> ReadCandidates()
        {
            var candidates = new List<Candidate>();
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                // Only items with a name: a nameless one is a leftover the client cannot show.
                command.CommandText = "SELECT it.Id, it.Type, it.Data FROM ItemTemplates it " +
                                      "WHERE EXISTS (SELECT 1 FROM Translations t WHERE t.Key = CAST(it.NameId AS TEXT));";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    int type = reader.GetInt32(1);
                    if (!Types.ContainsKey(type)) continue;
                    using var data = JsonDocument.Parse(reader.GetString(2));
                    int level = data.RootElement.TryGetProperty("level", out var l) ? l.GetInt32() : 0;
                    int flags = data.RootElement.TryGetProperty("m_flags", out var f) ? f.GetInt32() : 0;
                    var rarity = RarityOf(type, flags);
                    if (rarity != null) candidates.Add(new Candidate(reader.GetInt32(0), level, rarity.Value));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RandomLoot] Could not read the items: {ex.Message}");
            }
            return candidates;
        }

        /// <summary>The items to drop from: the database's, or a test's.</summary>
        internal static void Use(IEnumerable<Candidate> candidates)
        {
            var pools = Enum.GetValues<Rarity>().ToDictionary(r => r, _ => new List<Candidate>());
            foreach (var c in candidates) pools[c.Rarity].Add(c);
            _highest = pools.Values.SelectMany(p => p).Select(c => c.Level).DefaultIfEmpty(0).Max();
            _pools = pools;
        }

        /// <summary>For the tests: forget the items read, so the next roll reads them again.</summary>
        internal static void Forget() => _pools = null;

        /// <summary>How many items of each rarity can drop, for the log.</summary>
        public static string Summary()
            => string.Join(", ", Pools.Where(p => p.Key != Rarity.Perfect).Select(p => $"{p.Value.Count} {p.Key}"));

        /// <summary>The rarity an item drops as, or null when the random loot never drops it.</summary>
        public static Rarity? RarityOf(int type, int flags)
        {
            if (!Types.TryGetValue(type, out var t)) return null;
            if (t.Category == CosmeticCategory) return Rarity.Cosmetic;
            if (EquipmentFamilies.Contains(t.Family))
            {
                if ((flags & Etheral) != 0) return null;
                return (flags & Legendary) != 0 ? Rarity.Legendary : Rarity.Equipment;
            }
            if (t.Family == CreatureFamily) return Rarity.Creature;
            if (type == DofusType) return Rarity.Dofus;
            if (t.Family == ConsumableFamily && (flags & (Usable | Saleable)) == (Usable | Saleable)) return Rarity.Consumable;
            return null;
        }

        /// <summary>
        /// Whether an item of this type is worn: equipment, mounts and pets, dofus and trophies,
        /// cosmetics. Those drop as pieces of their own, each with its effects rolled.
        /// </summary>
        public static bool IsWorn(int type)
            => Types.TryGetValue(type, out var t)
               && (EquipmentFamilies.Contains(t.Family) || t.Family == CreatureFamily
                   || t.Family == DofusFamily || t.Category == CosmeticCategory);

        // ─── The roll ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// What a beaten monster drops besides its table: nothing most of the time, and at the
        /// chance given, two to five items around its level.
        /// </summary>
        public static List<Drop> Roll(int monsterLevel, double chancePercent, Random dice)
        {
            var drops = new List<Drop>();
            if (dice.NextDouble() * 100.0 >= chancePercent) return drops;

            // Monsters past the last item level drop the last level's items.
            int level = Math.Min(monsterLevel, Math.Max(1, Highest()));
            int count = dice.Next(FewestItems, MostItems + 1);
            for (int i = 0; i < count; i++)
            {
                var drop = Pick(level, dice);
                if (drop != null) drops.Add(drop.Value);
            }
            return drops;
        }

        /// <summary>The highest level an item that can drop has.</summary>
        private static int Highest()
        {
            _ = Pools;
            return _highest;
        }

        /// <summary>The rarity a roll from 0 to 100 lands on.</summary>
        public static Rarity RarityFor(double roll)
        {
            double edge = 0;
            foreach (var (rarity, percent) in Odds)
            {
                edge += percent;
                if (roll < edge) return rarity;
            }
            return Rarity.Consumable;
        }

        /// <summary>One item of a handful: a rarity rolled, and an item of it at the monster's level.</summary>
        public static Drop? Pick(int monsterLevel, Random dice)
        {
            var rarity = RarityFor(dice.NextDouble() * 100.0);
            return PickIn(rarity, monsterLevel, dice)
                   ?? (rarity != Rarity.Equipment ? PickIn(Rarity.Equipment, monsterLevel, dice) : null)
                   ?? (rarity != Rarity.Consumable ? PickIn(Rarity.Consumable, monsterLevel, dice) : null);
        }

        /// <summary>Whether an item's level suits a monster's, for its rarity.</summary>
        public static bool Fits(Rarity rarity, int itemLevel, int monsterLevel) => rarity switch
        {
            Rarity.Dofus => Math.Abs(itemLevel - monsterLevel) <= DofusLevelWindow,
            Rarity.Creature or Rarity.Cosmetic => itemLevel <= monsterLevel + LevelWindow,
            _ => Math.Abs(itemLevel - monsterLevel) <= LevelWindow,
        };

        private static Drop? PickIn(Rarity rarity, int level, Random dice)
        {
            var pool = Pools[rarity == Rarity.Perfect ? Rarity.Equipment : rarity];
            var fits = pool.Where(c => Fits(rarity, c.Level, level)).ToList();
            if (fits.Count == 0) return null;
            if (rarity != Rarity.Perfect) return new Drop(fits[dice.Next(fits.Count)].Gid, rarity);

            // A perfect piece carries as its exo the AP or MP its template does not roll; one that
            // rolls both cannot be one, so the next is tried.
            int start = dice.Next(fits.Count);
            for (int i = 0; i < fits.Count; i++)
            {
                var candidate = fits[(start + i) % fits.Count];
                var template = Forgemagic.TemplateOf(candidate.Gid);
                if (template == null) continue;
                var missing = new[] { Forgemagic.ActionPoints, Forgemagic.MovementPoints }
                    .Where(e => (template.MaxOf(e) ?? 0) <= 0).ToList();
                if (missing.Count > 0) return new Drop(candidate.Gid, rarity, missing[dice.Next(missing.Count)]);
            }
            return null;
        }

        /// <summary>The effects a dropped piece is born with: rolled, or perfect with its exo.</summary>
        public static List<Equipment.ItemEffect> EffectsOf(Forgemagic.Template template, int exo, Random dice)
        {
            if (exo == 0) return Forgemagic.Roll(template, dice);
            var effects = Forgemagic.Perfect(template);
            effects.Add(new Equipment.ItemEffect(exo, 1, 0, 0));
            return effects;
        }
    }
}
