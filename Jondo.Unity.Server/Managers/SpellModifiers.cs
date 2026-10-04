using System;
using System.Collections.Generic;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The modifiers of one spell -- the catalogue's category 3, "#1: +#3 ..." with the spell in
    /// the dice -- as the fight honours them and as the client is told of them.
    /// </summary>
    /// <remarks>
    /// The engine keeps each one as a row on its bearer (<see cref="Buff.Sobre"/>, the
    /// <see cref="SpellAspect"/>), and the jxm of the row goes out like any other. What the
    /// client then COMPUTES with is the hnd: "f1 { f2: the action, f3: the total, f4: the
    /// modifier, f5: the spell }, f2: whose", and the hnk takes a modifier away. Both numberings
    /// are read off the captures, row against frame, same fighter and same spell:
    ///
    ///   modifier (f4)                      effect                 action (f2)
    ///    1 range modifiable                 282                    1
    ///    3 base damage                      293                    1          (1,444 rows)
    ///    5 AP cost                          285 / 296              1 / 2      (31 / 100)
    ///    7 critical                         287                    1
    ///    9 line of sight                    289                    2
    ///   10 casts per turn                   290                    1
    ///   11 casts per target                 291                    1
    ///   12 maximum range                    281 / 294 / 2905       1 / 2 / 3
    ///   13 minimum range                    280 / 295 / 2906       1 / 2 / 3
    ///   14 occupied cell needed             314 / 297              1 / 2
    ///   15 free cell needed                 299                    1
    ///   16 visible target needed            798                    1
    ///   19 base healing                     2935                   1
    ///
    /// So the action is add (1), take away (2) or set (3), and for the switches on (1) and
    /// off (2): Uginak's Bestialidad pins its spells' maximum range with "f2=3 f3=2 f4=12", the
    /// Tymador's bombs add to his spells' AP cost with "f2=2 f4=5", Eclipse switches line of
    /// sight off with "f2=2 f4=9". The total is the sum of the live rows -- two 289 on 23801
    /// go out as "f3=2" -- and a zero total is written without its f3. 286 (cast interval)
    /// has no hnd in any capture and is honoured by the server alone.
    /// </remarks>
    public static class SpellModifiers
    {
        /// <summary>The hnd action: add, take away, set.</summary>
        public const int Add = 1;
        public const int Subtract = 2;
        public const int Set = 3;

        private static readonly Dictionary<SpellAspect, (int Kind, int Action)> Wire = new()
        {
            [SpellAspect.RangeModifiable] = (1, Add),
            [SpellAspect.DanoBase] = (3, Add),
            [SpellAspect.ApCostDown] = (5, Add),
            [SpellAspect.ApCostUp] = (5, Subtract),
            [SpellAspect.CriticalUp] = (7, Add),
            [SpellAspect.LineOfSightOff] = (9, Subtract),
            [SpellAspect.CastsPerTurnUp] = (10, Add),
            [SpellAspect.CastsPerTargetUp] = (11, Add),
            [SpellAspect.AlcanceMaximo] = (12, Add),
            [SpellAspect.MaxRangeDown] = (12, Subtract),
            [SpellAspect.MaxRangeSet] = (12, Set),
            [SpellAspect.AlcanceMinimo] = (13, Add),
            [SpellAspect.MinRangeDown] = (13, Subtract),
            [SpellAspect.MinRangeSet] = (13, Set),
            [SpellAspect.OccupiedCellOn] = (14, Add),
            [SpellAspect.OccupiedCellOff] = (14, Subtract),
            [SpellAspect.FreeCellOn] = (15, Add),
            [SpellAspect.VisibleTargetOn] = (16, Add),
            [SpellAspect.BaseHeal] = (19, Add),
        };

        /// <summary>The modifier and the action a row of this aspect goes out as, or null.</summary>
        public static (int Kind, int Action)? OnTheWire(SpellAspect aspect)
            => Wire.TryGetValue(aspect, out var wire) ? wire : null;

        /// <summary>Whether the aspect pins a number rather than adding to it: a zero is a value.</summary>
        public static bool PinsAValue(SpellAspect aspect)
            => aspect is SpellAspect.MaxRangeSet or SpellAspect.MinRangeSet;

        /// <summary>What the hnd of this aspect carries for a spell right now: the pin, or the sum.</summary>
        public static int Total(Fighter who, int spell, SpellAspect aspect, int round)
            => PinsAValue(aspect)
                ? who.Buffs.FijadoDelHechizo(spell, aspect, round) ?? 0
                : who.Buffs.DelHechizo(spell, aspect, round);

        /// <summary>Whether a live row of this aspect still holds for the spell.</summary>
        public static bool Holds(Fighter who, int spell, SpellAspect aspect, int round)
            => who.Buffs.TieneDelHechizo(spell, aspect, round);

        // ─── What the cast honours ─────────────────────────────────────────────────

        /// <summary>The AP a cast costs: the level's, less 285, plus 296, never below zero.</summary>
        public static int ApCost(Fighter caster, int spell, int levelCost, int round)
            => Math.Max(0, levelCost
                           - caster.Buffs.DelHechizo(spell, SpellAspect.ApCostDown, round)
                           + caster.Buffs.DelHechizo(spell, SpellAspect.ApCostUp, round));

        /// <summary>
        /// The range a cast may reach: the minimum the level's plus 280 less 295, the maximum what
        /// the caller already added up (level, characteristic, 281) less 294; and a live 2906 or
        /// 2905 is the minimum or the maximum whatever else -- the Uginak's beast form "solo puede
        /// atacar hasta 2 AL máximo", in its class sheet, and its 13791 is pinned to 0-0.
        /// </summary>
        public static (int Min, int Max) Range(Fighter caster, int spell, int min, int max, int round)
        {
            min -= caster.Buffs.DelHechizo(spell, SpellAspect.MinRangeDown, round);
            max -= caster.Buffs.DelHechizo(spell, SpellAspect.MaxRangeDown, round);
            int? pinnedMin = caster.Buffs.FijadoDelHechizo(spell, SpellAspect.MinRangeSet, round);
            int? pinnedMax = caster.Buffs.FijadoDelHechizo(spell, SpellAspect.MaxRangeSet, round);
            if (pinnedMin.HasValue) min = pinnedMin.Value;
            if (pinnedMax.HasValue) max = pinnedMax.Value;
            return (Math.Max(0, min), Math.Max(0, max));
        }

        /// <summary>Casts per turn: the level's cap plus 290. No cap stays no cap.</summary>
        public static int CastsPerTurn(Fighter caster, int spell, int levelCap, int round)
            => levelCap > 0 ? levelCap + caster.Buffs.DelHechizo(spell, SpellAspect.CastsPerTurnUp, round) : 0;

        /// <summary>Casts per target: the level's cap plus 291. No cap stays no cap.</summary>
        public static int CastsPerTarget(Fighter caster, int spell, int levelCap, int round)
            => levelCap > 0 ? levelCap + caster.Buffs.DelHechizo(spell, SpellAspect.CastsPerTargetUp, round) : 0;

        /// <summary>The critical chance a spell's own rows add: 287.</summary>
        public static int Critical(Fighter caster, int spell, int round)
            => caster.Buffs.DelHechizo(spell, SpellAspect.CriticalUp, round);

        /// <summary>The cast interval, less 286.</summary>
        public static int CastInterval(Fighter caster, int spell, int levelInterval, int round)
            => Math.Max(0, levelInterval - caster.Buffs.DelHechizo(spell, SpellAspect.CastIntervalDown, round));

        /// <summary>
        /// What the aimed cell has to be: the level's two flags, a 314 switching "occupied" on and a
        /// 297 off, a 299 switching "free" on. Karcham lets the carrying Pandawa throw on an empty
        /// cell exactly so: 297 and 299 on 12787 while he carries.
        /// </summary>
        public static (bool NeedFree, bool NeedTaken) Cells(Fighter caster, int spell, bool levelFree, bool levelTaken, int round)
        {
            bool taken = (levelTaken || Holds(caster, spell, SpellAspect.OccupiedCellOn, round))
                         && !Holds(caster, spell, SpellAspect.OccupiedCellOff, round);
            bool free = levelFree || Holds(caster, spell, SpellAspect.FreeCellOn, round);
            return (free, taken);
        }

        /// <summary>The spell's own basic healing bonus: 2935, the heal's 293.</summary>
        public static int BaseHeal(Fighter caster, int spell, int round)
            => caster.Buffs.DelHechizo(spell, SpellAspect.BaseHeal, round);
    }
}
