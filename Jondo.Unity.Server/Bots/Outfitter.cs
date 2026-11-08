using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Server.Managers;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Bots
{
    /// <summary>
    /// What a simulated player should wear: the best pieces of its level for its element, sets
    /// completed when their bonuses pay for what they replace. The pieces come from what can be
    /// crafted and from what is on sale, the two ways a player gets equipment.
    /// </summary>
    /// <remarks>
    /// Asked for on 2026-11-08: no bot wore anything, and they should wear good sets, crafted or
    /// bought, and change them as they level. The score of a piece is what its lines are worth to
    /// a character of that element: its characteristic first, then AP, MP and range, its
    /// element's damage, life, resistances, and what the other elements' lines give it, which is
    /// little. A malus counts against.
    /// </remarks>
    internal static class Outfitter
    {
        /// <summary>A piece of equipment, as the planner weighs it.</summary>
        internal sealed record Piece(int Gid, int Type, int Level, int SetId, bool TwoHanded, long Price,
                                     IReadOnlyList<Forgemagic.TemplateLine> Lines);

        /// <summary>What a bot plans to wear: slot to piece.</summary>
        internal sealed class Plan
        {
            public Dictionary<int, Piece> Slots { get; } = new();
            public double Score { get; set; }
        }

        // ─── Slots ───────────────────────────────────────────────────────────────────────────

        public const int Amulet = 0, Weapon = 1, LeftRing = 2, Belt = 3, RightRing = 4, Boots = 5,
                         Hat = 6, Cloak = 7, Shield = 15;

        /// <summary>The weapon types: bows, wands, staves, daggers, swords, hammers, shovels, axes, scythes, lances.</summary>
        private static readonly HashSet<int> WeaponTypes = new() { 2, 3, 4, 5, 6, 7, 8, 19, 22, 271 };

        /// <summary>The slots an item type is worn in; empty for what the bots do not wear.</summary>
        internal static IReadOnlyList<int> SlotsOf(int type) => type switch
        {
            1 => new[] { Amulet },
            9 => new[] { LeftRing, RightRing },
            10 => new[] { Belt },
            11 => new[] { Boots },
            16 => new[] { Hat },
            17 => new[] { Cloak },
            82 => new[] { Shield },
            _ when WeaponTypes.Contains(type) => new[] { Weapon },
            _ => Array.Empty<int>(),
        };

        /// <summary>The item types the bots wear.</summary>
        internal static readonly int[] WornTypes = { 1, 9, 10, 11, 16, 17, 82, 2, 3, 4, 5, 6, 7, 8, 19, 22, 271 };

        // ─── What a line is worth ────────────────────────────────────────────────────────────

        /// <summary>The bots' four elements, by the characteristic they put their capital in.</summary>
        internal enum Element { Intelligence, Chance, Agility, Strength }

        // The characteristics of the Effects table (its Characteristic column).
        private const int Ap = 1, Strength = 10, Vitality = 11, Wisdom = 12, Chance = 13, Agility = 14,
                          Intelligence = 15, Damage = 16, Critical = 18, Range = 19, Mp = 23, Power = 25,
                          Summons = 26, Pods = 40, Initiative = 44, Prospecting = 48, Heals = 49,
                          EarthDamage = 88, FireDamage = 89, WaterDamage = 90, AirDamage = 91, NeutralDamage = 92;

        private static int StatOf(Element element) => element switch
        {
            Element.Intelligence => Intelligence,
            Element.Chance => Chance,
            Element.Agility => Agility,
            _ => Strength,
        };

        private static int DamageOf(Element element) => element switch
        {
            Element.Intelligence => FireDamage,
            Element.Chance => WaterDamage,
            Element.Agility => AirDamage,
            _ => EarthDamage,
        };

        /// <summary>What one point of a characteristic is worth to a character of this element.</summary>
        internal static double WeightOf(int characteristic, Element element)
        {
            if (characteristic == StatOf(element) || characteristic == Power) return 1.0;
            if (characteristic == DamageOf(element) || characteristic == Damage) return 4.0;
            return characteristic switch
            {
                Ap => 90, Mp => 80, Range => 40, Summons => 15,
                Strength or Intelligence or Chance or Agility => 0.1,
                Vitality => 0.25, Wisdom => 0.2, Critical => 6, 86 => 2, Heals => 1,
                EarthDamage or FireDamage or WaterDamage or AirDamage => 0.3, NeutralDamage => 0.5,
                >= 33 and <= 37 => 5, >= 54 and <= 58 => 1.5,
                78 or 79 or 82 or 83 => 1.5, 27 or 28 => 1, 84 => 1, 85 => 0.5, 87 => 0.8,
                Initiative => 0.08, Prospecting => 0.2, Pods => 0.02,
                _ => 0,
            };
        }

        /// <summary>Each effect's characteristic and whether it is a malus, from the Effects table.</summary>
        private static readonly Lazy<Dictionary<int, (int Characteristic, bool Malus)>> Effects = new(ReadEffects);

        private static Dictionary<int, (int, bool)> ReadEffects()
        {
            var effects = new Dictionary<int, (int, bool)>();
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Id, Characteristic, BonusType FROM Effects;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    effects[reader.GetInt32(0)] = (reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                                                   !reader.IsDBNull(2) && reader.GetInt32(2) < 0);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bots] The effects could not be read: {ex.Message}");
            }
            return effects;
        }

        /// <summary>For tests: an effect declared by hand.</summary>
        internal static void Declare(int effect, int characteristic, bool malus = false)
            => Effects.Value[effect] = (characteristic, malus);

        /// <summary>What a value of an effect is worth.</summary>
        internal static double Worth(int effect, double value, Element element)
        {
            if (!Effects.Value.TryGetValue(effect, out var known) || known.Characteristic == 0) return 0;
            double worth = WeightOf(known.Characteristic, element) * value;
            return known.Malus ? -worth : worth;
        }

        /// <summary>What a piece is worth, each line at the middle of its roll.</summary>
        internal static double Score(Piece piece, Element element)
            => piece.Lines.Sum(l => Worth(l.Effect, (l.Min + l.Max) / 2.0, element));

        private static double SetBonus(IEnumerable<Piece> worn, Element element)
            => ItemSets.BonusesFor(worn.Select(p => p.Gid)).Sum(b => Worth(b.Effect, b.Value, element));

        // ─── The pieces ──────────────────────────────────────────────────────────────────────

        /// <summary>Every piece a bot can craft: a recipe of a worn type, wearable by anybody.</summary>
        internal static readonly Lazy<IReadOnlyList<Piece>> Craftable = new(ReadCraftable);

        private static IReadOnlyList<Piece> ReadCraftable()
        {
            var pieces = new List<Piece>();
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT t.Id, t.Type, t.Data FROM Recipes r JOIN ItemTemplates t ON t.Id = r.ResultId " +
                                      $"WHERE r.ResultTypeId IN ({string.Join(",", WornTypes)});";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var piece = PieceOf(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), craft: true);
                    if (piece != null) pieces.Add(piece);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bots] The craftable equipment could not be read: {ex.Message}");
            }
            return pieces;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> _prices = new();

        /// <summary>An item's own price, the template's: what the game says it is worth, at least one kama.</summary>
        internal static long BasePrice(int gid) => _prices.GetOrAdd(gid, ReadPrice);

        private static long ReadPrice(int gid)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Data FROM ItemTemplates WHERE Id = $gid;";
                command.Parameters.AddWithValue("$gid", gid);
                if (command.ExecuteScalar() is not string data) return 1;
                using var doc = JsonDocument.Parse(data);
                return doc.RootElement.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Number
                    ? Math.Max(1, (long)p.GetDouble()) : 1;
            }
            catch (Exception)
            {
                return 1;
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Piece?> _pieces = new();

        /// <summary>Any item as a piece, read once; null when it is not one a bot wears.</summary>
        internal static Piece? PieceOf(int gid) => _pieces.GetOrAdd(gid, ReadPiece);

        private static Piece? ReadPiece(int gid)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Type, Data FROM ItemTemplates WHERE Id = $gid;";
                command.Parameters.AddWithValue("$gid", gid);
                using var reader = command.ExecuteReader();
                return reader.Read() ? PieceOf(gid, reader.GetInt32(0), reader.GetString(1), craft: false) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// A template as a piece, when a bot wears its type and anybody may: no criterion to wear it
        /// -- a class, a characteristic, a quest --, nor, for a recipe, to craft it.
        /// </summary>
        private static Piece? PieceOf(int gid, int type, string data, bool craft)
        {
            if (SlotsOf(type).Count == 0) return null;
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            string Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            int Number(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            if (Text("criterions").Length > 0) return null;
            if (craft && Text("craftFeasibleCriterion").Length > 0) return null;
            var template = Forgemagic.TemplateOf(gid);
            if (template == null) return null;
            bool twoHanded = root.TryGetProperty("twoHanded", out var two)
                             && (two.ValueKind == JsonValueKind.True || (two.ValueKind == JsonValueKind.Number && two.GetInt32() != 0));
            long price = root.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Number ? (long)p.GetDouble() : 0;
            var piece = new Piece(gid, type, Number("level"), Number("itemSetId"), twoHanded, price, template.Lines);
            _pieces[gid] = piece;
            return piece;
        }

        // ─── The plan ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The best outfit of a level and element out of these pieces: the best piece of each slot,
        /// then a set put on over it, and a second one over the slots the first leaves, whenever
        /// what the set gives beats what it takes off.
        /// </summary>
        internal static Plan Best(int level, Element element, IEnumerable<Piece> pool)
        {
            var eligible = pool.Where(p => p.Level <= level && p.Level > 0).GroupBy(p => p.Gid).Select(g => g.First()).ToList();
            var scores = eligible.ToDictionary(p => p.Gid, p => Score(p, element));
            double S(Piece p) => scores.TryGetValue(p.Gid, out var s) ? s : Score(p, element);

            var plan = Single(eligible, S);
            Rate(plan, element, S);

            var bySet = eligible.Where(p => p.SetId > 0).GroupBy(p => p.SetId).Where(g => g.Count() >= 2).ToList();
            for (int round = 0; round < 2; round++)
            {
                var fixedSlots = round == 0 ? new HashSet<int>() : SetSlots(plan);
                Plan? better = null;
                foreach (var set in bySet)
                {
                    var tried = WithSet(plan, set.ToList(), fixedSlots, S);
                    if (tried == null) continue;
                    Rate(tried, element, S);
                    if (tried.Score > (better?.Score ?? plan.Score) + 0.01) better = tried;
                }
                if (better == null) break;
                plan = better;
            }
            return plan;
        }

        /// <summary>The best piece of every slot on its own; two different rings.</summary>
        private static Plan Single(List<Piece> eligible, Func<Piece, double> score)
        {
            var plan = new Plan();
            foreach (int slot in new[] { Amulet, Belt, Boots, Hat, Cloak })
            {
                var best = eligible.Where(p => SlotsOf(p.Type)[0] == slot).OrderByDescending(score).FirstOrDefault();
                if (best != null && score(best) > 0) plan.Slots[slot] = best;
            }
            var rings = eligible.Where(p => p.Type == 9).OrderByDescending(score).Where(p => score(p) > 0).Take(2).ToList();
            if (rings.Count > 0) plan.Slots[LeftRing] = rings[0];
            if (rings.Count > 1) plan.Slots[RightRing] = rings[1];

            var weapons = eligible.Where(p => SlotsOf(p.Type)[0] == Weapon).OrderByDescending(score).ToList();
            var shield = eligible.Where(p => p.Type == 82).OrderByDescending(score).FirstOrDefault();
            var oneHand = weapons.FirstOrDefault(w => !w.TwoHanded);
            var twoHands = weapons.FirstOrDefault(w => w.TwoHanded);
            double withShield = (oneHand == null ? 0 : Math.Max(0, score(oneHand))) + (shield == null ? 0 : Math.Max(0, score(shield)));
            double alone = twoHands == null ? 0 : score(twoHands);
            if (alone > withShield && twoHands != null) plan.Slots[Weapon] = twoHands;
            else
            {
                if (oneHand != null && score(oneHand) > 0) plan.Slots[Weapon] = oneHand;
                if (shield != null && score(shield) > 0) plan.Slots[Shield] = shield;
            }
            return plan;
        }

        /// <summary>The slots held by a piece of a set that is giving a bonus.</summary>
        private static HashSet<int> SetSlots(Plan plan)
            => plan.Slots.Where(s => s.Value.SetId > 0 && plan.Slots.Values.Count(o => o.SetId == s.Value.SetId) >= 2)
                         .Select(s => s.Key).ToHashSet();

        /// <summary>
        /// The plan with as many of a set's pieces as fit put on, each in its slot, outside the
        /// slots kept; null when fewer than two fit.
        /// </summary>
        private static Plan? WithSet(Plan plan, List<Piece> set, HashSet<int> kept, Func<Piece, double> score)
        {
            var tried = new Plan();
            foreach (var (slot, piece) in plan.Slots) tried.Slots[slot] = piece;
            var placed = new HashSet<int>();
            foreach (var piece in set.OrderByDescending(score))
            {
                // The same piece twice is no use: a set counts each of its pieces once.
                if (tried.Slots.Values.Any(p => p.Gid == piece.Gid)) continue;
                var free = SlotsOf(piece.Type).Where(s => !kept.Contains(s) && !placed.Contains(s)).ToList();
                if (free.Count == 0) continue;
                // Of two ring slots, the one with the weaker ring.
                int slot = free.OrderBy(s => tried.Slots.TryGetValue(s, out var on) ? score(on) : double.MinValue).First();
                tried.Slots[slot] = piece;
                placed.Add(slot);
                if (slot == Weapon && piece.TwoHanded) tried.Slots.Remove(Shield);
                if (slot == Shield && tried.Slots.TryGetValue(Weapon, out var weapon) && weapon.TwoHanded) tried.Slots.Remove(Weapon);
            }
            return placed.Count >= 2 ? tried : null;
        }

        private static void Rate(Plan plan, Element element, Func<Piece, double> score)
            => plan.Score = plan.Slots.Values.Sum(score) + SetBonus(plan.Slots.Values, element);
    }
}
