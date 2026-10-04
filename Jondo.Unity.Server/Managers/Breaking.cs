using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The grinder's other skill: breaking items into runes.
    /// </summary>
    /// <remarks>
    /// ─── How many runes ─────────────────────────────────────────────────────────────────────
    ///
    /// Every characteristic of the item gives runes of its own base rune -- the smallest one of
    /// that characteristic: Fu for strength, Vi (+5) for vitality, Ini (+10) for initiative:
    ///
    ///     runes = (3 · value · weight · item level / 200 + 1) · coefficient / weight of the rune
    ///
    /// and the fraction left is the chance of one more. It is the community's formula, and it
    /// holds on the seven lines of the grinder capture (Interactivos varios/"triturador-romper
    /// objetos"), each with the coefficient the server itself reported in kfp:
    ///
    ///     Amuleto campesino, level 20, 40.3%   initiative 64 -> 1.18 Ini   got 1
    ///                                          chance 11     -> 1.73 Sue   got 2
    ///                                          evasion 3     -> 0.46 Hui   got 0
    ///                                          water damage 2 -> 0.32      got 0
    ///     Capa de esponja,   level 20, 65.5%   chance 25     -> 5.57 Sue   got 6
    ///                                          agility 15    -> 3.60 Agi   got 3
    ///     Broche Heta,       level 28, 236%    wisdom 17     -> 17.6 Sa    got 18
    ///                                          intelligence 2 -> 4.34 Inte got 5
    ///
    /// Maluses, weapon damage and characteristics without a rune give nothing.
    ///
    /// ─── Focus ──────────────────────────────────────────────────────────────────────────────
    ///
    /// Focusing a characteristic turns the others into it: the focused line keeps its own
    /// weight and takes half of every other's, all of it given in runes of the focused
    /// characteristic, the others giving nothing. That is how players describe it on the official
    /// forum ("le poids des autres caractéristiques est pris en compte, à 50 %"); no capture
    /// focuses. An item without the focused characteristic breaks as if there were no focus.
    ///
    /// ─── The coefficient ────────────────────────────────────────────────────────────────────
    ///
    /// On the official servers it is per item and moves with how much the whole server breaks it,
    /// between 1% and 4000%; nobody outside Ankama knows the rule. Here each template starts at
    /// 100%, every unit broken takes 1% of what it has, and it grows back 2 points an hour up to
    /// 100%. It lives in memory, like the resources' regrowth.
    /// </remarks>
    public static class Breaking
    {
        /// <summary>"Romper objetos", the grinder's second skill.</summary>
        public const int Skill = 181;

        public const double FullCoefficient = 1.0;
        public const double LeastCoefficient = 0.01;

        /// <summary>What a unit broken takes off its template's coefficient, as a share of it.</summary>
        public const double DropPerUnit = 0.01;

        /// <summary>What the coefficient grows back each hour, up to full.</summary>
        public const double RecoveryPerHour = 0.02;

        private sealed class Coefficient
        {
            public double Value = FullCoefficient;
            public DateTime At = DateTime.UtcNow;
        }

        private static readonly ConcurrentDictionary<int, Coefficient> _coefficients = new ConcurrentDictionary<int, Coefficient>();
        private static Dictionary<int, Forgemagic.Rune>? _baseRunes;
        private static readonly object _lock = new object();

        /// <summary>The coefficient an item template breaks at right now.</summary>
        public static double CoefficientOf(int gid, DateTime? now = null)
        {
            var c = _coefficients.GetOrAdd(gid, _ => new Coefficient());
            lock (c)
            {
                var when = now ?? DateTime.UtcNow;
                double hours = Math.Max(0, (when - c.At).TotalHours);
                if (c.Value < FullCoefficient)
                    c.Value = Math.Min(FullCoefficient, c.Value + hours * RecoveryPerHour);
                c.At = when;
                return c.Value;
            }
        }

        /// <summary>Some units of a template have just been broken.</summary>
        public static void Broke(int gid, int units)
        {
            var c = _coefficients.GetOrAdd(gid, _ => new Coefficient());
            lock (c) c.Value = Math.Max(LeastCoefficient, c.Value * Math.Pow(1 - DropPerUnit, units));
        }

        /// <summary>For tests: forget every coefficient.</summary>
        internal static void Forget() => _coefficients.Clear();

        /// <summary>For tests: the base runes declared by hand.</summary>
        internal static void Declare(params Forgemagic.Rune[] runes)
        {
            lock (_lock) _baseRunes = runes.ToDictionary(r => r.Effect);
        }

        /// <summary>The smallest rune of a characteristic: the one breaking gives.</summary>
        public static Forgemagic.Rune? BaseRuneOf(int effect)
        {
            var runes = BaseRunes();
            return runes.TryGetValue(effect, out var rune) ? rune : null;
        }

        private static Dictionary<int, Forgemagic.Rune> BaseRunes()
        {
            lock (_lock)
            {
                if (_baseRunes != null) return _baseRunes;
                var found = new Dictionary<int, Forgemagic.Rune>();
                try
                {
                    using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT Id FROM ItemTemplates WHERE Type = $type;";
                    command.Parameters.AddWithValue("$type", Forgemagic.RuneType);
                    var ids = new List<int>();
                    using (var reader = command.ExecuteReader())
                        while (reader.Read()) ids.Add(reader.GetInt32(0));

                    foreach (int gid in ids)
                    {
                        var rune = Forgemagic.RuneOf(gid);
                        if (rune == null) continue;
                        var r = rune.Value;
                        if (!found.TryGetValue(r.Effect, out var had) || r.Points < had.Points) found[r.Effect] = r;
                    }
                    Console.WriteLine($"[Breaking] {found.Count} characteristics with a base rune.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Breaking] Could not read the runes: {ex.Message}");
                }
                _baseRunes = found;
                return found;
            }
        }

        /// <summary>
        /// The runes one unit of an item gives at a coefficient: rune template -> how many. The
        /// fraction of each line is rolled for one more.
        /// </summary>
        public static Dictionary<int, int> Yield(int level, IEnumerable<Equipment.ItemEffect> effects,
                                                 double coefficient, Random dice, int focus = 0)
        {
            // What each line weighs once broken, and the rune it turns into.
            var lines = new List<(int Effect, double Weight, Forgemagic.Rune Rune)>();
            foreach (var line in effects)
            {
                if (line.Effect <= 0 || line.Text != null || line.DiceNum != 0 || line.DiceSide != 0 || line.Value <= 0)
                    continue;
                var info = Forgemagic.InfoOf(line.Effect);
                if (info.Weight <= 0 || info.IsMalus || info.IsWeaponDamage) continue;
                var rune = BaseRuneOf(line.Effect);
                if (rune == null || rune.Value.Weight <= 0) continue;
                lines.Add((line.Effect, (3.0 * line.Value * info.Weight * level / 200 + 1) * coefficient, rune.Value));
            }

            if (focus != 0 && lines.Any(l => l.Effect == focus))
            {
                var focused = lines.First(l => l.Effect == focus);
                double weight = lines.Sum(l => l.Effect == focus ? l.Weight : l.Weight * FocusShare);
                lines = new List<(int, double, Forgemagic.Rune)> { (focus, weight, focused.Rune) };
            }

            var runes = new Dictionary<int, int>();
            foreach (var (_, weight, rune) in lines)
            {
                double count = weight / rune.Weight;
                int whole = (int)Math.Floor(count);
                if (dice.NextDouble() < count - whole) whole++;
                if (whole <= 0) continue;
                runes[rune.Gid] = (runes.TryGetValue(rune.Gid, out int had) ? had : 0) + whole;
            }
            return runes;
        }

        /// <summary>What a focused breaking keeps of every other characteristic.</summary>
        public const double FocusShare = 0.5;

        /// <summary>Whether an item gives anything at all: a characteristic with a rune of its own.</summary>
        public static bool Breakable(IEnumerable<Equipment.ItemEffect> effects)
            => effects.Any(e => e.Effect > 0 && e.Text == null && e.Value > 0 && Forgemagic.InfoOf(e.Effect).Weight > 0
                                && !Forgemagic.InfoOf(e.Effect).IsWeaponDamage && BaseRuneOf(e.Effect) != null);
    }
}
