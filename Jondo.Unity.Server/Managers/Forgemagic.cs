using Jondo.Unity.Launcher;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Smithmagic: what a rune does to an item, and how likely it is to do it.
    /// </summary>
    /// <remarks>
    /// ─── What is measured ───────────────────────────────────────────────────────────────────
    ///
    /// The WIRE is measured: 114 runes applied by a level-200 magus in two captures (Oficios/
    /// "envio invitacion a maguear ..."). Each one answers kdr { f2 { f1: pool change, f3: pool,
    /// f4: the item }, f3: 1 failure / 2 success }, and the numbers in them say how the pool and
    /// the losses work:
    ///
    ///   the weight of a point is the client's own effectPowerRate (EffectsDataRoot): vitality
    ///   0.2, strength 1, AP 100... A Vi rune (+5 vitality) weighs 1.
    ///
    ///   a failure that costs "19 -> 13 vitality" (1.2 of weight) for a rune of weight 1 leaves
    ///   0.2 in the pool: pool += weight lost - weight of the rune. A later partial success
    ///   whose rune weighs 1 takes the 0.2 of the pool first and 0.8 (4 vitality) off the item.
    ///
    /// ─── What is not ────────────────────────────────────────────────────────────────────────
    ///
    /// The ODDS are a server rule, and Ankama never published them: nothing in the client carries
    /// them. The model below is the one the community describes, with its constants in the open:
    ///
    ///   a weak item                66% clean, 34% partial,  0% failure
    ///   an item at its perfect jet 43% clean, 50% partial,  7% failure
    ///   the floor, beyond that     15% clean, 50% partial, 35% failure
    ///   exo AP, MP or range         1% clean, and only one of the three per item
    ///   over and exo               never past a weight of 101 on one characteristic, unless
    ///                              the item's own maximum is already heavier
    ///
    /// plus one rule for the rune's own reach, calibrated with the owner of the server: a +1
    /// strength rune enters well up to 25-30 strength, a +3 up to 50, a +10 up to 100 or more, and
    /// a +1 is all but hopeless from 99 to 100. That is a reach of 30·√(rune weight): 30, 52 and
    /// 95. Past its reach a rune's clean chance fades, down to 1% at 3.3 times it.
    ///
    /// The captures agree well enough to keep it: in range 70% of the runes entered (43% clean),
    /// over the maximum 43% (28% clean).
    ///
    /// ─── The pool travels with the item ─────────────────────────────────────────────────────
    ///
    /// It is kept as one more line of the item's effects, <see cref="PoolEffect"/>, in hundredths
    /// of weight. The client never sees it (it is not a real effect and it is left off the wire),
    /// but it lives where the item lives: the bag, a chest, a bank, the next owner.
    /// </remarks>
    public static class Forgemagic
    {
        /// <summary>The hidden line that keeps the pool, in hundredths of weight.</summary>
        public const int PoolEffect = -1;

        /// <summary>Item type of the runes.</summary>
        public const int RuneType = 78;

        /// <summary>The signature rune: "Runa de firma".</summary>
        public const int SignatureRune = 7508;

        /// <summary>"Fabricado por: #4", the crafter's signature.</summary>
        public const int CraftedBy = 988;

        /// <summary>"Modificado por: #4", the magus' signature ('Sacri-Master' in the captures).</summary>
        public const int ModifiedBy = 985;

        public const int ActionPoints = 111;
        public const int MovementPoints = 128;
        public const int Range = 117;

        /// <summary>No characteristic weighs more than this, over or exo.</summary>
        public const double WeightCap = 101;

        public const double WeakClean = 0.66;
        public const double PerfectClean = 0.43;
        public const double FloorClean = 0.15;
        public const double PerfectFailure = 0.07;
        public const double FloorFailure = 0.35;

        /// <summary>How far past its perfect jet an item falls to the floor: half its weight.</summary>
        public const double FloorPastPerfect = 0.5;

        /// <summary>A rune's reach, in weight: this times the square root of its own weight.</summary>
        public const double ReachCoefficient = 30;

        /// <summary>At this many times its reach a rune's clean chance is gone (down to 1%).</summary>
        public const double ReachFade = 3.3;

        public const double LeastClean = 0.01;

        /// <summary>What is left of the clean chance above the maximum, before the cap bites.</summary>
        public const double OverClean = 0.65;

        /// <summary>What is left of it on a characteristic the item does not have.</summary>
        public const double ExoClean = 0.3;

        public const double ExoApMpRange = 0.01;

        /// <summary>The share of failures that take nothing away.</summary>
        public const double HarmlessFailure = 0.4;

        // ─── The client's effect catalogue ──────────────────────────────────────────────────

        /// <summary>What smithmagic needs to know of an effect.</summary>
        public readonly struct EffectInfo
        {
            public EffectInfo(double weight, int category, bool useDice, int opposite, int bonusType)
            {
                Weight = weight; Category = category; UseDice = useDice; Opposite = opposite;
                BonusType = bonusType;
            }

            /// <summary>effectPowerRate: what a point weighs.</summary>
            public double Weight { get; }
            public int Category { get; }
            public bool UseDice { get; }
            /// <summary>The effect that takes away what this one gives, and the other way round.</summary>
            public int Opposite { get; }
            public int BonusType { get; }
            public bool IsWeaponDamage => Category == 2;

            /// <summary>A characteristic that takes away: its weight is negative (-0.5 a point of -strength).</summary>
            public bool IsMalus => BonusType < 0 || Weight < 0;
        }

        private static readonly Dictionary<int, EffectInfo> _effects = new Dictionary<int, EffectInfo>();

        public static int EffectCount => _effects.Count;

        public static void Initialize()
        {
            _effects.Clear();
            _templates.Clear();
            string path = Paths.EffectWeightsJson;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Forgemagic] {Path.GetFileName(path)} is missing; no rune can weigh anything.");
                return;
            }

            try
            {
                // { "125": [weight, category, useDice, oppositeId, bonusType], ... }
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out int id)) continue;
                    var v = entry.Value;
                    _effects[id] = new EffectInfo(v[0].GetDouble(), v[1].GetInt32(), v[2].GetInt32() != 0,
                                                  v[3].GetInt32(), v[4].GetInt32());
                }
                Console.WriteLine($"[Forgemagic] {_effects.Count} effects, " +
                                  $"{_effects.Values.Count(e => e.Weight > 0)} with a weight.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Forgemagic] Could not read {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        public static EffectInfo InfoOf(int effect) => _effects.TryGetValue(effect, out var e) ? e : default;

        /// <summary>What a point of this effect weighs.</summary>
        public static double WeightOf(int effect) => InfoOf(effect).Weight;

        /// <summary>For tests: an effect declared by hand.</summary>
        internal static void Declare(int effect, double weight, int opposite = 0, int bonusType = 1,
                                     int category = 0, bool useDice = true)
            => _effects[effect] = new EffectInfo(weight, category, useDice, opposite, bonusType);

        // ─── Item templates ─────────────────────────────────────────────────────────────────

        /// <summary>A line an item template can roll: the effect and its range.</summary>
        public readonly record struct TemplateLine(int Effect, int Min, int Max, int Value);

        /// <summary>What the template of an item says: its level, its type and its lines.</summary>
        public sealed class Template
        {
            public int Gid { get; init; }
            public int Level { get; init; }
            public int Type { get; init; }
            public IReadOnlyList<TemplateLine> Lines { get; init; } = Array.Empty<TemplateLine>();

            /// <summary>The highest a characteristic can roll, or null when the template has none.</summary>
            public int? MaxOf(int effect)
            {
                foreach (var line in Lines)
                    if (line.Effect == effect) return line.Max;
                return null;
            }
        }

        private static readonly ConcurrentDictionary<int, Template?> _templates = new ConcurrentDictionary<int, Template?>();

        /// <summary>The template of an item, read once.</summary>
        public static Template? TemplateOf(int gid) => _templates.GetOrAdd(gid, ReadTemplate);

        /// <summary>For tests: a template declared by hand.</summary>
        internal static void Declare(Template template) => _templates[template.Gid] = template;

        private static Template? ReadTemplate(int gid)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Type, Data FROM ItemTemplates WHERE Id = $gid;";
                command.Parameters.AddWithValue("$gid", gid);
                using var reader = command.ExecuteReader();
                if (!reader.Read()) return null;
                int type = reader.GetInt32(0);
                using var doc = JsonDocument.Parse(reader.GetString(1));
                int level = doc.RootElement.TryGetProperty("level", out var l) ? l.GetInt32() : 0;

                var lines = new List<TemplateLine>();
                foreach (var e in DatabaseManager.GetItemEffectsData(DatabaseManager.GetItemTemplatePossibleEffects(gid)))
                {
                    if (e.EffectId == 0) continue;
                    int min = e.DiceNum != 0 ? e.DiceNum : e.Value;
                    int max = e.DiceSide != 0 ? e.DiceSide : min;
                    if (max < min) (min, max) = (max, min);
                    lines.Add(new TemplateLine(e.EffectId, min, max, e.Value));
                }
                return new Template { Gid = gid, Level = level, Type = type, Lines = lines };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Forgemagic] Could not read the template of {gid}: {ex.Message}");
                return null;
            }
        }

        // ─── Crafting: the roll of a new item ───────────────────────────────────────────────

        /// <summary>
        /// The effects of a freshly crafted item, each ordinary characteristic rolled once in its
        /// template's range: two Plussains (8537) are two different rolls of 4 to 6 strength.
        /// Weapon damage keeps its range and a compound effect its three numbers.
        /// </summary>
        public static List<Equipment.ItemEffect> Roll(Template template, Random dice)
        {
            var effects = new List<Equipment.ItemEffect>();
            foreach (var line in template.Lines)
            {
                var info = InfoOf(line.Effect);
                if (info.IsWeaponDamage && line.Max != line.Min)
                {
                    effects.Add(new Equipment.ItemEffect(line.Effect, 0, line.Min, line.Max));
                    continue;
                }
                if (info.UseDice || info.Weight != 0)
                {
                    int rolled = line.Max > line.Min ? dice.Next(line.Min, line.Max + 1) : line.Max;
                    effects.Add(new Equipment.ItemEffect(line.Effect, rolled, 0, 0));
                    continue;
                }
                // A compound effect -- the spell of a dofus, a title -- travels with its numbers.
                effects.Add(new Equipment.ItemEffect(line.Effect, line.Value, line.Min, line.Max));
            }
            return effects;
        }

        /// <summary>Whether two copies of this template can share a stack: nothing in it rolls.</summary>
        public static bool Stacks(Template template)
            => template.Lines.All(l => l.Min == l.Max || InfoOf(l.Effect).IsWeaponDamage);

        // ─── The rune ───────────────────────────────────────────────────────────────────────

        /// <summary>What a rune adds: one characteristic and how many points of it.</summary>
        public readonly record struct Rune(int Gid, int Effect, int Points)
        {
            public double Weight => Points * WeightOf(Effect);
        }

        /// <summary>The rune an item is, or null when it is no stat rune.</summary>
        public static Rune? RuneOf(int gid)
        {
            var template = TemplateOf(gid);
            if (template == null || template.Type != RuneType) return null;
            foreach (var line in template.Lines)
            {
                if (WeightOf(line.Effect) > 0 && line.Max > 0) return new Rune(gid, line.Effect, line.Max);
            }
            return null;
        }

        // ─── The odds ───────────────────────────────────────────────────────────────────────

        public enum Outcome
        {
            /// <summary>The rune enters and nothing else moves.</summary>
            Clean,

            /// <summary>The rune enters, and the item loses its weight elsewhere.</summary>
            Partial,

            /// <summary>The rune does not enter; the item may lose its weight anyway.</summary>
            Failure,
        }

        /// <summary>What happened to the pool, as kdr's f2.f1 says it.</summary>
        public enum PoolChange
        {
            Same = 0,
            Up = 1,
            Down = 2,
        }

        public readonly record struct Odds(double Clean, double Partial, double Failure);

        /// <summary>
        /// The characteristic lines of an item, the ones a rune can move: bonuses, and the maluses a
        /// rune eats. Weapon damage weighs too (2 a point) but a rune never touches it.
        /// </summary>
        private static bool IsStatLine(Equipment.ItemEffect e)
            => e.Effect > 0 && e.Text == null && e.DiceNum == 0 && e.DiceSide == 0 && WeightOf(e.Effect) != 0
               && !InfoOf(e.Effect).IsWeaponDamage;

        /// <summary>What an item weighs: every characteristic at its weight, the maluses taking away.</summary>
        public static double WeightOfItem(IEnumerable<Equipment.ItemEffect> effects)
            => effects.Where(IsStatLine).Sum(e => e.Value * WeightOf(e.Effect));

        /// <summary>What the item would weigh with every line at its maximum.</summary>
        public static double MaxWeightOf(Template template)
            => template.Lines.Where(l => WeightOf(l.Effect) != 0 && !InfoOf(l.Effect).IsWeaponDamage)
                             .Sum(l => l.Max * WeightOf(l.Effect));

        private static bool IsApMpRange(int effect)
            => effect == ActionPoints || effect == MovementPoints || effect == Range;

        /// <summary>How likely a rune is to enter this item.</summary>
        public static Odds OddsOf(Template template, IReadOnlyList<Equipment.ItemEffect> effects, Rune rune)
        {
            double w = WeightOf(rune.Effect);
            if (w <= 0) return new Odds(0, 0, 1);
            int current = (int)effects.Where(e => e.Effect == rune.Effect && IsStatLine(e)).Sum(e => e.Value);
            int after = current + rune.Points;
            int? max = template.MaxOf(rune.Effect);

            // A rune that eats the item's opposite malus is no exo, even when the template only
            // rolls the malus: +strength on an item that carries -strength.
            int opposite = InfoOf(rune.Effect).Opposite;
            bool eatsMalus = opposite != 0 && InfoOf(opposite).IsMalus
                             && effects.Any(e => e.Effect == opposite && IsStatLine(e) && e.Value > 0);
            if ((max == null || max <= 0) && eatsMalus)
            {
                var (malusClean, malusFailure) = Base(template, effects);
                return Normalised(malusClean, malusFailure);
            }

            // Exo: a characteristic the template does not roll.
            if (max == null || max <= 0)
            {
                if (after * w > WeightCap) return new Odds(0, 0, 1);
                if (IsApMpRange(rune.Effect))
                {
                    bool anotherExo = effects.Any(e => IsApMpRange(e.Effect) && e.Effect != rune.Effect
                                                       && (template.MaxOf(e.Effect) ?? 0) <= 0 && e.Value > 0);
                    if (anotherExo || current > 0) return new Odds(0, 0, 1);
                    return new Odds(ExoApMpRange, 0, 1 - ExoApMpRange);
                }
                var (baseClean, baseFailure) = Base(template, effects);
                double clean = baseClean * ExoClean * (1 - after * w / WeightCap);
                double partial = (1 - baseClean - baseFailure) * ExoClean;
                return Normalised(clean, 1 - clean - partial);
            }

            var (c, f) = Base(template, effects);
            double cleanOdds = c, failureOdds = f, partialOdds = 1 - c - f;

            // The rune's reach: past 30·√weight of the characteristic it fades.
            double reach = ReachCoefficient * Math.Sqrt(rune.Weight);
            double x = current * w / reach;
            if (x > 1)
            {
                double fade = Math.Clamp(1 - (x - 1) / (ReachFade - 1), 0, 1);
                double kept = Math.Min(cleanOdds, Math.Max(LeastClean, cleanOdds * fade));
                double removed = cleanOdds - kept;
                cleanOdds = kept;
                partialOdds += removed / 2;
                failureOdds += removed / 2;
            }

            // Over: past the template's maximum, never past the cap.
            if (after > max.Value)
            {
                double maxWeight = max.Value * w;
                double cap = Math.Max(WeightCap, maxWeight);
                if (after * w > cap) return new Odds(0, 0, 1);
                double share = (after * w - maxWeight) / Math.Max(cap - maxWeight, 1e-9);
                double kept = cleanOdds * OverClean * (1 - share);
                double removed = cleanOdds - kept;
                cleanOdds = kept;
                partialOdds += removed / 3;
                failureOdds += removed * 2 / 3;
            }

            return Normalised(cleanOdds, failureOdds);
        }

        /// <summary>The odds before the rune's own reach: how full the item is.</summary>
        internal static (double Clean, double Failure) Base(Template template, IReadOnlyList<Equipment.ItemEffect> effects)
        {
            double max = MaxWeightOf(template);
            double fill = max > 0 ? WeightOfItem(effects) / max : 1;
            if (fill <= 1)
            {
                fill = Math.Max(0, fill);
                return (WeakClean - (WeakClean - PerfectClean) * fill, PerfectFailure * fill);
            }
            double t = Math.Min(1, (fill - 1) / FloorPastPerfect);
            return (PerfectClean - (PerfectClean - FloorClean) * t,
                    PerfectFailure + (FloorFailure - PerfectFailure) * t);
        }

        private static Odds Normalised(double clean, double failure)
        {
            clean = Math.Clamp(clean, 0, 1);
            failure = Math.Clamp(failure, 0, 1 - clean);
            return new Odds(clean, 1 - clean - failure, failure);
        }

        // ─── Applying it ────────────────────────────────────────────────────────────────────

        /// <summary>What a rune did.</summary>
        public sealed class Result
        {
            public Outcome Outcome { get; init; }
            public PoolChange PoolChange { get; init; }
            /// <summary>The pool afterwards, in weight.</summary>
            public double Pool { get; init; }
            public List<Equipment.ItemEffect> Effects { get; init; } = new List<Equipment.ItemEffect>();
            public Odds Odds { get; init; }
            /// <summary>What the item lost, effect by effect, in points.</summary>
            public Dictionary<int, int> Lost { get; init; } = new Dictionary<int, int>();
            public bool Succeeded => Outcome != Outcome.Failure;
        }

        /// <summary>The pool an item carries, in weight.</summary>
        public static double PoolOf(IEnumerable<Equipment.ItemEffect> effects)
            => effects.Where(e => e.Effect == PoolEffect).Sum(e => e.Value) / 100.0;

        /// <summary>Rolls a rune on an item and says what came of it. The effects are not touched.</summary>
        public static Result Apply(Template template, IReadOnlyList<Equipment.ItemEffect> effects, Rune rune, Random dice)
        {
            var odds = OddsOf(template, effects, rune);
            double roll = dice.NextDouble();
            var outcome = roll < odds.Clean ? Outcome.Clean
                        : roll < odds.Clean + odds.Partial ? Outcome.Partial
                        : Outcome.Failure;
            return Resolve(template, effects, rune, outcome, odds, dice);
        }

        /// <summary>
        /// What an outcome does to the item, with the pool paying first. Kept apart from the roll
        /// so a test can ask for one outcome and check the arithmetic against the captures.
        /// </summary>
        public static Result Resolve(Template template, IReadOnlyList<Equipment.ItemEffect> effects, Rune rune,
                                     Outcome outcome, Odds odds, Random dice)
        {
            var lines = new Dictionary<int, int>();
            var order = new List<int>();
            var others = new List<Equipment.ItemEffect>();
            foreach (var e in effects)
            {
                if (e.Effect == PoolEffect) continue;
                if (!IsStatLine(e)) { others.Add(e); continue; }
                if (!lines.ContainsKey(e.Effect)) order.Add(e.Effect);
                lines[e.Effect] = lines.TryGetValue(e.Effect, out int had) ? had + (int)e.Value : (int)e.Value;
            }
            double pool = PoolOf(effects);
            double poolBefore = pool;
            var lost = new Dictionary<int, int>();

            if (outcome != Outcome.Failure) Add(lines, order, rune);

            bool loses = outcome == Outcome.Partial
                         || (outcome == Outcome.Failure && dice.NextDouble() >= HarmlessFailure);
            if (loses)
                pool = Lose(template, lines, rune, rune.Weight, pool, lost, dice,
                            spareTarget: outcome == Outcome.Partial);

            var result = new List<Equipment.ItemEffect>(others);
            foreach (int effect in order)
            {
                int v = lines[effect];
                if (v != 0) result.Add(new Equipment.ItemEffect(effect, v, 0, 0));
            }
            int hundredths = (int)Math.Round(pool * 100);
            if (hundredths > 0) result.Add(new Equipment.ItemEffect(PoolEffect, hundredths, 0, 0));

            var change = Math.Abs(pool - poolBefore) < 0.005 ? PoolChange.Same
                       : pool > poolBefore ? PoolChange.Up : PoolChange.Down;
            return new Result
            {
                Outcome = outcome, PoolChange = change, Pool = hundredths / 100.0, Effects = result,
                Odds = odds, Lost = lost,
            };
        }

        /// <summary>The rune's points go in, eating the opposite malus first.</summary>
        private static void Add(Dictionary<int, int> lines, List<int> order, Rune rune)
        {
            int points = rune.Points;
            int opposite = InfoOf(rune.Effect).Opposite;
            if (opposite != 0 && InfoOf(opposite).IsMalus && lines.TryGetValue(opposite, out int malus) && malus > 0)
            {
                int eaten = Math.Min(malus, points);
                lines[opposite] = malus - eaten;
                points -= eaten;
            }
            if (points <= 0) return;
            if (!lines.ContainsKey(rune.Effect)) order.Add(rune.Effect);
            lines[rune.Effect] = (lines.TryGetValue(rune.Effect, out int had) ? had : 0) + points;
        }

        /// <summary>
        /// The item loses <paramref name="weight"/>: out of the pool first, then point by point
        /// off its characteristics, over-maximum ones first, the rune's own last. Whatever a whole
        /// point takes beyond the weight goes back to the pool.
        /// </summary>
        private static double Lose(Template template, Dictionary<int, int> lines, Rune rune, double weight,
                                   double pool, Dictionary<int, int> lost, Random dice, bool spareTarget)
        {
            if (pool >= weight - 1e-9) return pool - weight;
            double need = weight - pool;
            pool = 0;

            while (need > 1e-9)
            {
                var candidates = lines.Where(kv => kv.Value > 0 && !InfoOf(kv.Key).IsMalus
                                                    && (kv.Key != rune.Effect || !spareTarget))
                                      .Select(kv => kv.Key).ToList();
                if (candidates.Count == 0 && spareTarget)
                    candidates = lines.Where(kv => kv.Value > 0 && kv.Key == rune.Effect).Select(kv => kv.Key).ToList();
                if (candidates.Count == 0) break;

                // Over-maximum lines go first; among the rest, any one.
                var over = candidates.Where(k => lines[k] > (template.MaxOf(k) ?? 0)).ToList();
                var from = over.Count > 0 ? over : candidates;
                int effect = from[dice.Next(from.Count)];

                double w = WeightOf(effect);
                int points = Math.Min(lines[effect], Math.Max(1, (int)Math.Ceiling(need / w - 1e-9)));
                lines[effect] -= points;
                lost[effect] = (lost.TryGetValue(effect, out int had) ? had : 0) + points;
                need -= points * w;
            }
            if (need < 0) pool += -need;
            return pool;
        }

        // ─── Transcendence ──────────────────────────────────────────────────────────────────

        /// <summary>Item type of the transcendence runes, the Infinite Dreams' "Runa Ta/Buta/Suta".</summary>
        public const int TranscendenceType = 211;

        /// <summary>"Ninguna forjamagia futura": what a transcendence leaves on the item.</summary>
        public const int NoMoreSmithmagic = 2825;

        /// <summary>The transcendence's density: what the line may already weigh is 101 minus this.</summary>
        public const int TranscendenceDensity = 2826;

        /// <summary>"#1% de probabilidades de éxito": 100 on all 81 of them.</summary>
        public const int SuccessChance = 2827;

        /// <summary>A transcendence rune: the characteristic, its points, its density and its chance.</summary>
        public readonly record struct Transcendence(int Gid, int Effect, int Points, int Density, int Chance);

        /// <summary>The transcendence rune an item is, or null.</summary>
        /// <remarks>
        /// Its template says it all: the characteristic (+10 strength for a Ta Fu), 2827 "100% de
        /// probabilidades de éxito", 2825 "Ninguna forjamagia futura", and 2826, which has no text
        /// and is 40 for a Ta, 60 for a Buta, 80 for a Suta. That last one is the density rule the
        /// community's table follows to the unit: the line the rune goes onto may already weigh
        /// 101 minus it -- 61, 41 and 21 strength; 8, 5 and 3 AP reduction; 12, 8 and 4 elemental
        /// damage; 6 and 2 critical.
        /// </remarks>
        public static Transcendence? TranscendenceOf(int gid)
        {
            var template = TemplateOf(gid);
            if (template == null || template.Type != TranscendenceType) return null;
            int density = 0, chance = 100, effect = 0, points = 0;
            foreach (var line in template.Lines)
            {
                if (line.Effect == TranscendenceDensity) density = line.Max;
                else if (line.Effect == SuccessChance) chance = line.Max;
                else if (line.Effect != NoMoreSmithmagic && WeightOf(line.Effect) > 0 && line.Max > 0)
                {
                    effect = line.Effect;
                    points = line.Max;
                }
            }
            return effect == 0 ? null : new Transcendence(gid, effect, points, density, chance);
        }

        /// <summary>
        /// Why a transcendence cannot go onto this item, or null when it can: never twice, never on
        /// an item over its maximum or with an exo, and never onto a line that already weighs more
        /// than 101 minus the rune's density.
        /// </summary>
        public static string? TranscendenceRefusal(Template template, IReadOnlyList<Equipment.ItemEffect> effects, Transcendence rune)
        {
            if (effects.Any(e => e.Effect == NoMoreSmithmagic)) return "the item takes no smithmagic any more";
            foreach (var e in effects)
            {
                if (!IsStatLine(e) || InfoOf(e.Effect).IsMalus || e.Value <= 0) continue;
                int? max = template.MaxOf(e.Effect);
                if (max == null || max <= 0) return $"exo {e.Effect}";
                if (e.Value > max) return $"over {e.Effect} ({e.Value} over {max})";
            }
            double already = effects.Where(e => e.Effect == rune.Effect && IsStatLine(e)).Sum(e => e.Value) * WeightOf(rune.Effect);
            if (already > WeightCap - rune.Density + 1e-9)
                return $"the line weighs {already:0.##}, more than {WeightCap - rune.Density}";
            return null;
        }

        /// <summary>A transcendence that goes on: its points, and the item closed to smithmagic.</summary>
        public static Result Transcend(IReadOnlyList<Equipment.ItemEffect> effects, Transcendence rune)
        {
            var lines = new Dictionary<int, int>();
            var order = new List<int>();
            var others = new List<Equipment.ItemEffect>();
            foreach (var e in effects)
            {
                if (e.Effect == PoolEffect || !IsStatLine(e)) { others.Add(e); continue; }
                if (!lines.ContainsKey(e.Effect)) order.Add(e.Effect);
                lines[e.Effect] = lines.TryGetValue(e.Effect, out int had) ? had + (int)e.Value : (int)e.Value;
            }
            Add(lines, order, new Rune(rune.Gid, rune.Effect, rune.Points));

            var result = new List<Equipment.ItemEffect>(others.Where(o => o.Effect != PoolEffect));
            foreach (int effect in order)
                if (lines[effect] != 0) result.Add(new Equipment.ItemEffect(effect, lines[effect], 0, 0));
            result.Add(new Equipment.ItemEffect(NoMoreSmithmagic, 0, 0, 0));
            result.AddRange(others.Where(o => o.Effect == PoolEffect));
            return new Result
            {
                Outcome = Outcome.Clean, PoolChange = PoolChange.Same, Pool = PoolOf(effects),
                Effects = result, Odds = new Odds(1, 0, 0),
            };
        }

        /// <summary>The effects of an item with a signature in place of whatever it had.</summary>
        public static List<Equipment.ItemEffect> Signed(IEnumerable<Equipment.ItemEffect> effects, int signature, string name)
        {
            var result = effects.Where(e => e.Effect != signature).ToList();
            result.Add(new Equipment.ItemEffect(signature, 0, 0, 0, name));
            return result;
        }

        /// <summary>The effects the way the database keeps them: [[effect, value, diceNum, diceSide(, text)], ...].</summary>
        public static string Serialize(IEnumerable<Equipment.ItemEffect> effects)
        {
            var parts = new List<string>();
            foreach (var e in effects)
            {
                string numbers = $"{e.Effect},{e.Value},{e.DiceNum},{e.DiceSide}";
                parts.Add(e.Text == null ? $"[{numbers}]" : $"[{numbers},{JsonSerializer.Serialize(e.Text)}]");
            }
            return "[" + string.Join(",", parts) + "]";
        }
    }
}
