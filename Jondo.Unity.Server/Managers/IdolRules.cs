using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The fight rules of the quest idols (content/fights/idol_rules.json): what a fight is like when
    /// one of its characters holds an idol's alteration.
    /// </summary>
    /// <remarks>
    /// The Turquoise Dofus's idols (1660-1662) are used in their boss room and set an alteration for
    /// one fight (811). The client only describes that fight, in the idol's spell; its rules are the
    /// old idols' (Trespeó, Caracho, Hóskar...), whose spells it still carries with their rows. So a
    /// rule is a list of attitudes: «enemies» on each monster of the fight as it begins, «allies» on
    /// each character. The rows no old idol has at the right number are written under the idol's
    /// own spell (<see cref="SpellEffects.Write"/>).
    /// </remarks>
    public static class IdolRules
    {
        public const string File = "fights/idol_rules.json";

        /// <summary>A spell put as an attitude, at a grade.</summary>
        public readonly record struct Attitude(int Spell, int Grade);

        public sealed class Rule
        {
            /// <summary>The alteration the idol sets.</summary>
            public int Alteration { get; init; }

            /// <summary>The idol, for the log.</summary>
            public int Idol { get; init; }

            /// <summary>On each monster of the fight as it begins.</summary>
            public IReadOnlyList<Attitude> Enemies { get; init; } = Array.Empty<Attitude>();

            /// <summary>On each character of the fight.</summary>
            public IReadOnlyList<Attitude> Allies { get; init; } = Array.Empty<Attitude>();
        }

        /// <summary>A spell grade's rows written by the file, as SpellLevels' EffectsJson.</summary>
        public sealed record WrittenSpell(int Spell, int Grade, string EffectsJson);

        private static readonly Dictionary<int, Rule> _rules = new();
        private static readonly List<WrittenSpell> _spells = new();

        private static volatile bool _loaded;
        private static readonly object _lock = new object();

        /// <summary>Reads the file once, at boot, with its log line.</summary>
        public static void Load() => Ensure();

        /// <summary>Drops what was read and the rows it wrote, so the next question reads the file again.</summary>
        public static void Forget()
        {
            lock (_lock)
            {
                _rules.Clear();
                _spells.Clear();
                SpellEffects.ForgetWritten();
                _loaded = false;
            }
        }

        /// <summary>The rule of an alteration, or null.</summary>
        public static Rule? Of(int alteration)
        {
            Ensure();
            return _rules.TryGetValue(alteration, out var rule) ? rule : null;
        }

        /// <summary>Every rule read.</summary>
        public static IReadOnlyCollection<Rule> All
        {
            get
            {
                Ensure();
                return _rules.Values;
            }
        }

        /// <summary>
        /// The rules of the alterations these characters hold, each once however many of them
        /// hold it.
        /// </summary>
        public static List<Rule> HeldBy(IEnumerable<long> characters, Func<long, int, bool> holds)
        {
            Ensure();
            var held = new List<Rule>();
            foreach (long character in characters.Distinct())
            {
                foreach (var rule in _rules.Values)
                {
                    if (!held.Contains(rule) && holds(character, rule.Alteration)) held.Add(rule);
                }
            }
            return held;
        }

        /// <summary>
        /// Puts the rules of the fight's idols on its fighters as it begins: the enemies' attitudes
        /// on every monster there, the allies' on every character. Summons born later are left as
        /// they are. Returns how many attitudes were put.
        /// </summary>
        /// <param name="monstersSide">Whether a fighter is on the monsters' side.</param>
        /// <param name="holds">Whether a character holds an alteration (Alterations.Has).</param>
        public static int PutOn(FightInstance fight, Func<Fighter, bool> monstersSide, Func<long, int, bool> holds)
        {
            var fighters = fight.Azul.Concat(fight.Rojo).Where(f => f != null).ToList();
            var characters = fighters.Where(f => !f.IsMonster && !f.EsInvocado && f.Id > 0).ToList();
            if (characters.Count == 0) return 0;

            var rules = HeldBy(characters.Select(c => c.Id), holds);
            if (rules.Count == 0) return 0;

            int put = 0;
            foreach (var rule in rules)
            {
                foreach (var fighter in fighters)
                {
                    if (fighter.EsInvocado) continue;
                    bool monster = monstersSide(fighter);
                    var attitudes = monster ? rule.Enemies : fighter.IsMonster ? Array.Empty<Attitude>() : rule.Allies;
                    foreach (var attitude in attitudes)
                    {
                        if (fighter.Buffs.Actitudes.Contains(attitude.Spell)) continue;
                        fighter.Buffs.PonerActitud(attitude.Spell, attitude.Grade, givenInRound: 1);
                        put++;
                    }
                }
                Console.WriteLine($"[Idols] Fight #{fight.FightId}: the rules of idol {rule.Idol} " +
                                  $"(alteration {rule.Alteration}) on its fighters.");
            }
            return put;
        }

        private static void Ensure()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                try
                {
                    Read();
                }
                finally
                {
                    _loaded = true;   // a missing file counts as tried
                }
            }
        }

        private static void Read()
        {
            string path = Paths.ContentFile(File);
            if (!System.IO.File.Exists(path)) return;

            try
            {
                var (rules, spells) = Parse(System.IO.File.ReadAllText(path));
                foreach (var rule in rules) _rules[rule.Alteration] = rule;
                _spells.AddRange(spells);
                foreach (var spell in spells) SpellEffects.Write(spell.Spell, spell.Grade, spell.EffectsJson);
                Console.WriteLine($"[Idols] {_rules.Count} idol rule(s), {_spells.Count} written spell grade(s).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Idols] Could not read {File}: {ex.Message}");
            }
        }

        /// <summary>The rules and written spells of the file's text; a rule without an alteration is left out.</summary>
        public static (List<Rule> Rules, List<WrittenSpell> Spells) Parse(string json)
        {
            var rules = new List<Rule>();
            var spells = new List<WrittenSpell>();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("rules", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in list.EnumerateArray())
                {
                    var rule = new Rule
                    {
                        Alteration = Number(row, "alteration"),
                        Idol = Number(row, "idol"),
                        Enemies = Attitudes(row, "enemies"),
                        Allies = Attitudes(row, "allies"),
                    };
                    if (rule.Alteration > 0) rules.Add(rule);
                }
            }

            if (doc.RootElement.TryGetProperty("spells", out var written) && written.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in written.EnumerateArray())
                {
                    int spell = Number(row, "spell");
                    if (spell <= 0 || !row.TryGetProperty("effects", out var effects) || effects.ValueKind != JsonValueKind.Array) continue;
                    spells.Add(new WrittenSpell(spell, Math.Max(1, Number(row, "grade")), effects.GetRawText()));
                }
            }
            return (rules, spells);
        }

        private static List<Attitude> Attitudes(JsonElement row, string name)
        {
            var attitudes = new List<Attitude>();
            if (!row.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) return attitudes;
            foreach (var entry in list.EnumerateArray())
            {
                int spell = Number(entry, "spell");
                if (spell > 0) attitudes.Add(new Attitude(spell, Math.Max(1, Number(entry, "grade"))));
            }
            return attitudes;
        }

        private static int Number(JsonElement row, string name)
            => row.TryGetProperty(name, out var value) && value.TryGetInt32(out int number) ? number : 0;
    }
}
