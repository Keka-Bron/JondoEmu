using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The companions: what the item worn in the «Compagnon» slot (Equipment.CompanionSlot) brings
    /// into its master's fights -- its look, its characteristics at his level and its spells.
    /// </summary>
    /// <remarks>
    /// datos/companions_3.6.10.11.json, from the client's companionsdataroot,
    /// companioncharacteristicsdataroot and companionspellsdataroot bundles: 52 companions, each
    /// with its look («{7531|||110}» Lumino), its behaviour (startingSpellLevelId, a turn-end
    /// hook as a monster grade's), its characteristics as level ranges and its spells with the
    /// level each grade comes at («1,30;2,100;3,160»). The item says which companion it is in its
    /// effect 1161: the Signe de Lumino (14961) carries 1, all 52 items name one of the 52.
    ///
    /// A characteristic's ranges are [level, value] pairs: the one at level 1 is its base, each
    /// other one what a level adds up to that level, from the one before. Lumino's life, [1, 55]
    /// and [200, 5], is 55 plus 5 a level; its vitality, [200, 14.825] alone, 14.825 a level.
    /// INFERRED from the numbers: no capture of a companion's sheet.
    /// </remarks>
    public static class Companions
    {
        /// <summary>The item effect that names the companion an item is.</summary>
        public const int ItemEffect = 1161;

        public sealed record Companion(int Id, string Look, int StartingSpellLevelId,
                                       IReadOnlyList<(int Characteristic, IReadOnlyList<(double Level, double Value)> Ranges)> Characteristics,
                                       IReadOnlyList<(int Spell, IReadOnlyList<(int Grade, int Level)> Grades)> Spells);

        private static Dictionary<int, Companion> _all = new();

        public static int Count => _all.Count;

        public static void Initialize()
        {
            string path = Paths.Resolve("companions_3.6.10.11.json");
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Companions] {Path.GetFileName(path)} is missing: no companion comes into a fight.");
                return;
            }
            try
            {
                _all = Read(File.ReadAllText(path));
                Console.WriteLine($"[Companions] {_all.Count} companions.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Companions] Could not read {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        internal static Dictionary<int, Companion> Read(string json)
        {
            var all = new Dictionary<int, Companion>();
            using var doc = JsonDocument.Parse(json);
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                var characteristics = c.GetProperty("characteristics").EnumerateArray()
                    .Select(x => (x.GetProperty("carac").GetInt32(),
                                  (IReadOnlyList<(double, double)>)x.GetProperty("ranges").EnumerateArray()
                                      .Select(r => (r[0].GetDouble(), r[1].GetDouble())).ToList()))
                    .ToList();
                var spells = c.GetProperty("spells").EnumerateArray()
                    .Select(x => (x.GetProperty("spell").GetInt32(),
                                  (IReadOnlyList<(int, int)>)x.GetProperty("grades").EnumerateArray()
                                      .Select(g => (g[0].GetInt32(), g[1].GetInt32())).ToList()))
                    .ToList();
                int id = c.GetProperty("id").GetInt32();
                all[id] = new Companion(id, c.GetProperty("look").GetString() ?? "",
                                        c.GetProperty("startingSpellLevelId").GetInt32(), characteristics, spells);
            }
            return all;
        }

        public static Companion? Of(int id) => _all.TryGetValue(id, out var c) ? c : null;

        /// <summary>The companion an item is (its effect 1161), or zero.</summary>
        public static int IdOf(IEnumerable<Equipment.ItemEffect> effects)
            => (int)(effects.FirstOrDefault(e => e.Effect == ItemEffect).Value);

        /// <summary>The companion the character in session wears, or null.</summary>
        public static Companion? Worn()
        {
            var item = Equipment.InSlot(Equipment.CompanionSlot);
            return item == null ? null : Of(IdOf(item.Effects));
        }

        /// <summary>A characteristic at a level: its base, and what each range adds up to it.</summary>
        public static int ValueAt(IReadOnlyList<(double Level, double Value)> ranges, int level)
        {
            double total = 0;
            double from = 1;
            foreach (var (upTo, value) in ranges)
            {
                if (upTo <= 1)
                {
                    total += value;
                    continue;
                }
                total += value * Math.Max(0, Math.Min(level, upTo) - from);
                from = upTo;
            }
            return (int)Math.Floor(total + 1e-6);
        }

        /// <summary>A spell's grade at a level: the highest one reached, the first below them all.</summary>
        public static int GradeAt(IReadOnlyList<(int Grade, int Level)> grades, int level)
        {
            int grade = 1;
            foreach (var (g, from) in grades)
                if (level >= from && g > grade) grade = g;
            return grade;
        }
    }
}
