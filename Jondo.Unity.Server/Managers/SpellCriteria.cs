using System;
using System.Collections.Generic;
using Jondo.Unity.World.Fights;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What a spell level asks of the one who casts it, and which spell a level id is.
    /// </summary>
    /// <remarks>
    /// <c>SpellLevels.StatesCriterion</c> holds the states the caster must have or must not
    /// have: 3,121 conditions over the base, every one of them <c>HS=n</c> ("has state n") or
    /// <c>HS!n</c> ("has not"), joined with <c>&amp;</c> and <c>|</c> and sometimes bracketed --
    /// <c>(HS=3360|HS=3589)&amp;HS!7</c>. Conde Kontatrás is why it matters: Jaquemart is written
    /// <c>HS=56</c> and Multicuenta <c>HS!56</c>, one spell for when he is invulnerable and the
    /// other for when he is not.
    ///
    /// And the level ids: a monster's <c>startingSpellId</c> is a <c>SpellLevels.Id</c>, not a
    /// spell, so its behaviour spell and grade are read here.
    /// </remarks>
    public static class SpellCriteria
    {
        private static Dictionary<(int Spell, int Grade), string>? _criteria;
        private static Dictionary<int, (int Spell, int Grade)>? _levels;
        private static readonly object _lock = new();

        /// <summary>The level's condition on its caster's states, or empty when it has none.</summary>
        public static string Of(int spell, int grade)
        {
            Load();
            return _criteria!.TryGetValue((spell, Math.Max(1, grade)), out var criterion) ? criterion : "";
        }

        /// <summary>Whether the caster, as he stands now, may cast this spell at this grade.</summary>
        public static bool Allows(Fighter caster, int spell, int grade)
        {
            string criterion = Of(spell, grade);
            return criterion.Length == 0 || Holds(criterion, caster.Buffs.TieneEstado);
        }

        /// <summary>The spell and grade a <c>SpellLevels.Id</c> names; (0, 0) when none.</summary>
        public static (int Spell, int Grade) SpellOfLevel(int levelId)
        {
            if (levelId <= 0) return (0, 0);
            Load();
            return _levels!.TryGetValue(levelId, out var level) ? level : (0, 0);
        }

        /// <summary>
        /// Evaluates a condition against a state test: <c>HS=n</c> and <c>HS!n</c>, <c>&amp;</c>
        /// binding tighter than <c>|</c>, brackets as written. Anything it cannot read counts as
        /// met -- the client refuses the cast on its own side anyway, and a monster that never
        /// casts a spell is a worse mistake than one that casts it once too often.
        /// </summary>
        internal static bool Holds(string criterion, Func<int, bool> hasState)
        {
            if (string.IsNullOrWhiteSpace(criterion)) return true;
            int at = 0;
            try
            {
                bool result = Or(criterion, ref at, hasState);
                return result;
            }
            catch (FormatException)
            {
                return true;
            }
        }

        private static bool Or(string s, ref int at, Func<int, bool> hasState)
        {
            bool value = And(s, ref at, hasState);
            while (at < s.Length && s[at] == '|')
            {
                at++;
                bool right = And(s, ref at, hasState);
                value = value || right;
            }
            return value;
        }

        private static bool And(string s, ref int at, Func<int, bool> hasState)
        {
            bool value = Term(s, ref at, hasState);
            while (at < s.Length && s[at] == '&')
            {
                at++;
                bool right = Term(s, ref at, hasState);
                value = value && right;
            }
            return value;
        }

        private static bool Term(string s, ref int at, Func<int, bool> hasState)
        {
            while (at < s.Length && s[at] == ' ') at++;
            if (at < s.Length && s[at] == '(')
            {
                at++;
                bool inner = Or(s, ref at, hasState);
                if (at >= s.Length || s[at] != ')') throw new FormatException(s);
                at++;
                return inner;
            }

            if (at + 3 > s.Length || s[at] != 'H' || s[at + 1] != 'S') throw new FormatException(s);
            char op = s[at + 2];
            if (op != '=' && op != '!') throw new FormatException(s);
            at += 3;
            int start = at;
            while (at < s.Length && char.IsDigit(s[at])) at++;
            if (at == start) throw new FormatException(s);
            int state = int.Parse(s.AsSpan(start, at - start));
            bool has = hasState(state);
            return op == '=' ? has : !has;
        }

        private static void Load()
        {
            if (_criteria != null && _levels != null) return;
            lock (_lock)
            {
                if (_criteria != null && _levels != null) return;
                var criteria = new Dictionary<(int, int), string>();
                var levels = new Dictionary<int, (int, int)>();
                try
                {
                    using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT Id, SpellId, Grade, StatesCriterion FROM SpellLevels;";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        int id = (int)reader.GetInt64(0), spell = (int)reader.GetInt64(1), grade = (int)reader.GetInt64(2);
                        levels[id] = (spell, grade);
                        if (!reader.IsDBNull(3))
                        {
                            string criterion = reader.GetString(3).Trim();
                            if (criterion.Length > 0) criteria[(spell, grade)] = criterion;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Hechizos] Could not read the spell levels' conditions: {ex.Message}");
                }
                _levels = levels;
                _criteria = criteria;
            }
        }
    }
}
