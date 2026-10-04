using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The Rogue's combo: how hard a bomb hits when it finally goes off.
    /// </summary>
    /// <remarks>
    /// The client's own class sheet describes it: "Al principio de cada turno, el tymador dara 2
    /// combos a todas sus bombas presentes en el terreno. El combo aumenta de 1 a 15 maximo [...]
    /// Cuando se invoca una bomba, esta aparece en Combo I", and then lists the bonus of each
    /// step, 0% at I up to 360% at XV.
    ///
    /// NONE OF THAT IS WRITTEN HERE. It is all read out of spell 20497, "Combo", whose grade 1 is
    /// a ladder of eighteen rungs: a 950 that sets the first state when the bomb carries none, and
    /// then one <c>950 val=X mask=C,E&lt;Y&gt;</c> per step, each paired with a
    /// <c>792 dice=20500 max=N</c> and a 951 that takes the old state away. Spell 20500 grade N
    /// carries the percentage in the die of its 1027, "% de danos de combo", and those fifteen
    /// numbers are 20, 40, 60, 80, 100, 120, 140, 160, 190, 220, 250, 280, 320, 360, 360 -- the
    /// sheet's table exactly.
    ///
    /// The engine already walks that ladder on its own: casting 20497 on a bomb moves its state
    /// and hangs the 1027 on it. What was missing is that 1027 was only ever shown in the panel,
    /// so the combo made the bomb look bigger and hit exactly the same.
    ///
    /// The level is read from the STATE and not from the accumulated buffs on purpose. The ladder
    /// leaves exactly one state on the bomb, so the state is single-valued and self-correcting;
    /// the 1027 buffs pile up one per step, and adding them would make Combo IV worth
    /// 20+40+60 = 120% instead of 60%.
    ///
    /// Measured in "tymador-explobomba resiliente": three bombs over eight rounds, each one gets
    /// ONE combo on the round it is summoned and TWO every round after, without exception.
    /// </remarks>
    public static class Combo
    {
        /// <summary>The ladder spell. Casting it once on a bomb is one combo.</summary>
        public const int LadderSpell = 20497;

        /// <summary>The spell whose grades hold the percentage of each step.</summary>
        public const int BonusSpell = 20500;

        /// <summary>"% de danos de combo".</summary>
        private const int ComboDamagePercent = 1027;

        private const int SetState = 950;
        private const int SubCast = 792;

        private static readonly object _lock = new();
        private static Dictionary<int, int> _levelByState;     // state -> 1..15
        private static Dictionary<int, int> _percentByLevel;   // level -> 0..360
        private static Dictionary<int, int> _gradeByLevel;     // level -> grade of 20500

        /// <summary>Which rung of the ladder this fighter is on, or zero if it is on none.</summary>
        public static int LevelOf(Fighter who)
        {
            if (who == null) return 0;
            Build();
            int best = 0;
            foreach (int state in who.Buffs.Estados)
            {
                if (_levelByState.TryGetValue(state, out int level) && level > best) best = level;
            }
            return best;
        }

        /// <summary>What the combo adds to this fighter's damage, as a percentage.</summary>
        public static int PercentOf(Fighter who)
        {
            int level = LevelOf(who);
            if (level <= 0) return 0;
            Build();
            return _percentByLevel.TryGetValue(level, out int percent) ? percent : 0;
        }

        /// <summary>Whether this fighter is carrying a combo at all.</summary>
        public static bool Carries(Fighter who) => LevelOf(who) > 0;

        /// <summary>
        /// The highest rung a bomb may stand on.
        /// </summary>
        /// <remarks>
        /// "El combo aumenta de 1 a 15 maximo", says the client's class sheet, and the fifteenth
        /// is where the bonus stops growing: spell 20500 runs out of grades at 360% and the three
        /// rungs above it pay the same. The ladder in the spell does carry eighteen, and without
        /// this the bombs walked straight up to it -- measured in the log, both bombs sitting at
        /// level 18 while the client only knows how to draw I through XV.
        /// </remarks>
        public static int Tope => 15;

        /// <summary>Whether this state is one of the ladder's rungs.</summary>
        public static bool EsPeldano(int estado)
        {
            Build();
            return _levelByState.ContainsKey(estado);
        }

        /// <summary>Which rung this state is, or zero if it is not one.</summary>
        public static int NivelDelPeldano(int estado)
        {
            Build();
            return _levelByState.TryGetValue(estado, out int level) ? level : 0;
        }

        /// <summary>The states the ladder uses, lowest rung first. For tests and for the log.</summary>
        public static IReadOnlyList<int> Ladder()
        {
            Build();
            return _levelByState.OrderBy(pair => pair.Value).Select(pair => pair.Key).ToList();
        }

        /// <summary>Drops the cached ladder. Only tests need this.</summary>
        internal static void Forget()
        {
            lock (_lock) { _levelByState = null; _percentByLevel = null; _gradeByLevel = null; }
        }

        private static void Build()
        {
            lock (_lock)
            {
                if (_levelByState != null) return;

                var levels = new Dictionary<int, int>();
                var percents = new Dictionary<int, int> { [1] = 0 };
                var grades = new Dictionary<int, int>();
                var effects = SpellEffects.De(LadderSpell, 1).ToList();

                // The rung a bomb lands on when it carries no state yet: the only 950 of the
                // ladder that does not ask for a previous state.
                foreach (var effect in effects)
                {
                    if (effect.EffectId != SetState) continue;
                    if (PreviousState(effect.TargetMask) != 0) continue;
                    levels[effect.Value] = 1;
                    break;
                }

                // And then one step per "if you hold Y, take X instead", each with the grade of
                // 20500 that pays for it sitting right behind it.
                bool advanced = true;
                while (advanced)
                {
                    advanced = false;
                    for (int i = 0; i < effects.Count; i++)
                    {
                        var effect = effects[i];
                        if (effect.EffectId != SetState) continue;

                        int previous = PreviousState(effect.TargetMask);
                        if (previous == 0) continue;
                        if (!levels.TryGetValue(previous, out int before)) continue;
                        if (levels.ContainsKey(effect.Value)) continue;

                        int level = before + 1;
                        levels[effect.Value] = level;
                        advanced = true;

                        int grade = GradeRightAfter(effects, i);
                        if (grade > 0)
                        {
                            percents[level] = PercentOfGrade(grade);
                            grades[level] = grade;
                        }
                    }
                }

                _levelByState = levels;
                _percentByLevel = percents;
                _gradeByLevel = grades;

                Program.LogDebug($"[Combo] {levels.Count} peldanos leidos del hechizo " +
                                 $"{LadderSpell}; tope {percents.Values.DefaultIfEmpty(0).Max()}%.");
            }
        }

        /// <summary>The state an "E&lt;n&gt;" in the mask demands, or zero if it demands none.</summary>
        private static int PreviousState(string mask)
        {
            foreach (string piece in (mask ?? "").Split(','))
            {
                string t = piece.Trim().TrimStart('*');
                if (t.Length > 1 && t[0] == 'E' && int.TryParse(t.Substring(1), out int state))
                    return state;
            }
            return 0;
        }

        /// <summary>The grade of 20500 that the sub-cast right after this rung asks for.</summary>
        private static int GradeRightAfter(IReadOnlyList<SpellEffect> effects, int rung)
        {
            for (int i = rung + 1; i < effects.Count && i <= rung + 3; i++)
            {
                if (effects[i].EffectId == SubCast && effects[i].DiceNum == BonusSpell)
                    return effects[i].DiceSide;
            }
            return 0;
        }

        /// <summary>
        /// The grade of 20500 that pays for a rung. Zero for the first, which pays nothing.
        /// </summary>
        /// <remarks>
        /// Needed to tell the client which spell the bomb just cast on itself. Measured in
        /// "tymador-explobomba resiliente": rung 2 is announced with level 54102 (grade 1), rung 3
        /// with 54103 (grade 2), and so on up to rung 11 with 54476 (grade 10).
        /// </remarks>
        public static int GradeOf(int level)
        {
            Build();
            return _gradeByLevel.TryGetValue(level, out int grade) ? grade : 0;
        }

        /// <summary>The effect that makes a bomb grow.</summary>
        private const int ComboSize = 1060;

        /// <summary>A bomb size, as a percentage. A hundred is the size it was summoned at.</summary>
        /// <remarks>
        /// DERIVED, not tabulated. Every grade of 20500 carries a 1060 next to its 1027 -- 5, 10,
        /// 15, 20, 25, 30, 35, 40, 50, 60, 70, 80, 100, 120 -- and the size is a hundred plus the
        /// ones still on the bomb. Checked over every look change in the Rogue captures: 540 come
        /// out exactly that way, and the handful that do not are leftovers of an earlier fight in
        /// the same capture that never got their jya.
        ///
        /// It took the measurement to believe, because the sizes the real server sends look like
        /// no single grade of that list: 105, 110, 125, 130, 145, 150, 155, 175, 185, 210. The
        /// jumps of fifteen and twenty are TWO of them alive at once.
        ///
        /// AND TWO IS THE CEILING, which is the part that is not ours yet. Following bomb -5
        /// through "tymador-explobomba resiliente" buff by buff, the real server never holds more
        /// than two and ping-pongs which one it drops:
        ///
        ///   rung 2 [5]      3 [10]     4 [10,15]   5 [10,20]   6 [20,25]
        ///   rung 7 [20,30]  8 [20,35]  9 [35,40]  10 [35,50]  11 [50,60]
        ///
        /// That alternation is spells 30671 and 30672, the two halves of "Combo": each one is four
        /// 149 look swaps plus a 406 that wipes the OTHER, and 20500 sub-casts them at grades 3, 4,
        /// 8 and 9 while the rest carry a 666. Neither 30671 nor 30672 is implemented, so which of
        /// the two gets dropped cannot be reproduced yet; keeping the two most recent is as close
        /// as this gets without inventing the rule. It lands on the nose at rungs 4, 6 and 9 and
        /// runs one slot high at 5, 7, 8 and 10.
        ///
        /// Without the ceiling it would not run high, it would run absurd: every 1060 kept would
        /// put a rung-15 bomb at 760% of its size.
        /// </remarks>
        /// <summary>A bomb with no size buff: 100 percent.</summary>
        public const int BaseSize = 100;

        public static int SizeOf(Fighter who, int round)
        {
            if (who == null) return BaseSize;

            var live = new List<int>();
            foreach (var buff in who.Buffs.Puestos)
            {
                if (buff.EffectId == ComboSize && buff.Vivo(round)) live.Add(buff.Cuanto);
            }

            int total = 100;
            for (int i = System.Math.Max(0, live.Count - LiveSizeBuffs); i < live.Count; i++)
            {
                total += live[i];
            }
            return total;
        }

        /// <summary>How many 1060 the real server ever holds on one bomb at a time.</summary>
        private const int LiveSizeBuffs = 2;

        private static int PercentOfGrade(int grade)
        {
            foreach (var effect in SpellEffects.De(BonusSpell, grade))
            {
                if (effect.EffectId == ComboDamagePercent) return effect.DiceNum;
            }
            return 0;
        }
    }
}
