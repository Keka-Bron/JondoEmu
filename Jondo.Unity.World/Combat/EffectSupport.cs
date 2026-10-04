using System.Collections.Generic;

namespace Jondo.Unity.World.Combat
{
    /// <summary>What the fight engine actually does with an effect.</summary>
    public enum EffectSupportKind
    {
        /// <summary>The engine has code for it by name: pushes, summons, states, healing.</summary>
        Direct = 0,

        /// <summary>
        /// It changes a characteristic, and the engine applies it from the catalogue without
        /// knowing what it is.
        /// </summary>
        Characteristic = 1,

        /// <summary>
        /// Nothing happens. The client still writes it on the spell card, which is exactly what
        /// makes it dangerous.
        /// </summary>
        PanelOnly = 2,
    }

    /// <summary>
    /// Which of the game's 872 effects the engine can actually apply.
    /// </summary>
    /// <remarks>
    /// This is the information the architecture document calls the most useful thing in the whole
    /// spell module. Effect 108 — healing — is the reason this list matters: its catalogue row
    /// carries no characteristic, so it used to fall through to the panel-only branch even though
    /// the spell card said that it healed. Keeping it here makes the implementation explicit.
    ///
    /// There are two ways an effect can work, and they are not the same:
    ///
    /// <code>
    ///   Direct           the engine has code for it by name — a push, a summon, a state
    ///   Characteristic   its Effects row has Characteristic > 0, so the engine applies it
    ///                    generically without knowing what it is. 205 of 872 are like this.
    ///   PanelOnly        neither. It is drawn on the card and does nothing at all.
    /// </code>
    ///
    /// The ids live here rather than in the engine so that the editor and the engine cannot
    /// disagree: <c>EffectEngine</c> takes its own constants from this file, so a new effect
    /// gaining an implementation without appearing on this list would not compile.
    /// </remarks>
    public static class EffectSupport
    {
        // ─── The effects the engine implements by name ────────────────────────────

        /// <summary>Push away from the caster.</summary>
        public const int Push = 5;

        /// <summary>Pull towards the caster.</summary>
        public const int Pull = 6;

        /// <summary>
        /// 1103, "Empuja #1 casilla (sin daños)": the push of Patada and of 54 other spells,
        /// which is a 5 that never collides. In the Patada capture the bomb pushed three cells
        /// and the enemy pushed one both travel as a plain jwe 5 with no damage behind.
        /// </summary>
        public const int PushWithoutDamage = 1103;

        /// <summary>
        /// 783 "Hace retroceder hasta la casilla objetivo" and 1043 "Atrae hasta la casilla
        /// objetivo": the push and the pull whose length is the distance to the aimed cell. The
        /// one moved is the first fighter on the caster's line -- before the aimed cell for the
        /// push, beyond it for the pull. Measured on the Tymobot at 287: Empujoncito aimed at 260
        /// sends the bomb at 273 to 260, Aspirador aimed at 273 brings the bomb at 260 to 273,
        /// both as a plain jwe 5 with from and to.
        /// </summary>
        public const int PushToTargetCell = 783;
        public const int PullToTargetCell = 1043;

        /// <summary>
        /// 50 "Permite levantar al objetivo" and 51 "Lanza a un enemigo": carrying and throwing,
        /// the Pandawa's Karcham and Chamrak and the Tymobot's Pinzas alike. Measured on Pinzas:
        /// "jwe 50 f18{f1=273 f3=-13}" picks the bomb up from 273 and "jwe 51 f27{f1=-13 f2=260}"
        /// throws it to 260, with the bot walking onto 273 in between.
        /// </summary>
        public const int Carry = 50;
        public const int Throw = 51;

        /// <summary>
        /// The two states of a carry: 3 on the one carrying, 8 on the one carried. Read off
        /// Pinzas, whose cast condition is "HS=3|HS!8" -- may throw while carrying, may pick up
        /// while not carried -- and whose pick-up carries "*e3" (the caster is not carrying) and
        /// whose throw "*E3" (he is).
        /// </summary>
        public const int CarryingState = 3;
        public const int CarriedState = 8;

        /// <summary>
        /// 1097 "Crea ilusiones": the caster teleports to the aimed cell and leaves copies of
        /// himself on the cells symmetric to it around the cell he left. 1029 is the
        /// one that takes a copy away: at the caster's next turn start, all of them at once, and
        /// -- the class sheet -- the moment any of them takes damage. 150 is the visibility
        /// switch that goes with it: state 1 as the copies appear, 2 as they go.
        /// </summary>
        /// <remarks>
        /// Measured on the Tymadura capture: the Tymador on 230 aims at 257 (two cells up its
        /// axis), goes there, and the copies -8, -6, -7 appear on 259, 201 and 203 -- (+2,0),
        /// (-2,0) and (0,+2) from 230; (0,-2) is 257, where he now stands. At his next turn:
        /// jto 6, jwe 150 {2}, jwe 1029 per copy, jwi.
        /// </remarks>
        public const int Illusions = 1097;
        public const int IllusionGone = 1029;
        public const int Visibility = 150;
        public const int Teleport = 4;
        public const int SwapPositions = 8;

        /// <summary>1031, "Hace pasar de turno": Tymadura ends the caster's turn on the spot.</summary>
        public const int EndsTheTurn = 1031;

        /// <summary>
        /// 2027, "Toma el control de la entidad": a row on a summon that makes it its owner's to
        /// play (Fighter.PlaysOnItsOwn). The Steamer's Evolución III lays it on his turrets,
        /// the Sram's Doble on the double, Dominio de Invocaciones on the summons -- row 248585
        /// in the Steamer captures, family 0, for good.
        /// </summary>
        public const int TakesControl = 2027;

        /// <summary>
        /// 1033 and 1078, minus and plus a percentage of VITALITY. Measured on Último Aliento:
        /// -50% on 1150 vitality goes out as the sheet's vitality hole f8 = -575 and a buff of
        /// effect 153 ("-#1 vitalidad") worth 575, so the percentage is of the characteristic and
        /// the panel shows the flat number.
        /// </summary>
        public const int VitalityPercentMalus = 1033;
        public const int VitalityPercentBonus = 1078;
        public const int VitalityFlatMalus = 153;

        /// <summary>
        /// The bonus goes out as its own flat effect, 125 "+#1 vitalidad": Vitalidad's 20% on
        /// 1,150 vitality is "jxm 125 dice 230" in its capture, from 25215 at grade 5.
        /// </summary>
        public const int VitalityFlatBonus = 125;

        /// <summary>The caster steps back.</summary>
        public const int StepBack = 1041;

        /// <summary>The caster steps forward.</summary>
        public const int StepForward = 1042;

        /// <summary>Put a state on somebody.</summary>
        public const int AddState = 950;

        /// <summary>Take a state off.</summary>
        public const int RemoveState = 951;

        /// <summary>
        /// "Desactiva el estado #3": the state stays on the bearer, switched off for the row's
        /// duration -- Conde Kontatrás's Reloj de Bolsillo takes his Invulnerable (56) away for a
        /// turn this way, and it is back the turn after with nothing putting it again.
        /// </summary>
        public const int DisableState = 952;

        /// <summary>Cast another spell. This is the one that makes triggers work.</summary>
        public const int CastSpell = 792;

        /// <summary>
        /// Cast another spell from an ordinary spell effect. Class mechanics use this form while
        /// item attitudes mostly use <see cref="CastSpell"/>.
        /// </summary>
        public const int TriggerSpell = 1160;

        /// <summary>
        /// Execute the spell carried in DiceNum on the nearest eligible fighter in the effect
        /// area. Chained spells use states to exclude fighters already visited by the chain.
        /// </summary>
        public const int NearestTargetExecuteSpell = 2160;

        /// <summary>Remove every buff left by one spell.</summary>
        public const int RemoveSpellEffects = 406;

        /// <summary>Replace a fighter's visual appearance for the duration of the buff.</summary>
        public const int ChangeLook = 335;

        /// <summary>Summon a creature.</summary>
        public const int Summon = 181;

        /// <summary>
        /// Heal a fixed amount, of FIRE. Scaled by the element's characteristic and by the flat
        /// Heals one.
        /// </summary>
        /// <remarks>
        /// The element is not decoration and leaving it out of the name is how the other five get
        /// forgotten. The catalogue row for 108 reads "#1{{~1~2 a }}#2 de curas de fuego" with
        /// ElementId 2, and all 751 of its occurrences in SpellLevels declare effectElement 2 --
        /// not one is anything else.
        ///
        /// Its five siblings are the same effect in the other elements and NONE of them is
        /// implemented yet, which is worth writing down next to the one that is:
        ///
        /// <code>
        ///   2998  curas de agua              92 occurrences
        ///   2999  curas de aire              66
        ///   3000  curas de tierra            62
        ///   3001  curas neutrales            11
        ///   3002  curas del mejor elemento   30
        /// </code>
        /// </remarks>
        public const int FireHeal = 108;

        /// <summary>Heal a percentage of maximum life.</summary>
        public const int HealPercent = 1109;

        /// <summary>Kill outright.</summary>
        public const int Kill = 141;

        /// <summary>Damage effects run from 91 to 100 and carry their element in the catalogue.</summary>
        public const int FirstDamage = 91;

        public const int LastDamage = 100;

        /// <summary>
        /// Category 2 is the weapon-only effects, which the catalogue path deliberately skips.
        /// </summary>
        public const int WeaponCategory = 2;

        /// <summary>
        /// Everything the engine knows by name.
        /// </summary>
        /// <remarks>
        /// The damage range is expanded rather than left as a pair so that a caller can ask about
        /// one effect without knowing there is a range involved.
        /// </remarks>
        public static readonly IReadOnlySet<int> HandledDirectly = Build();

        private static HashSet<int> Build()
        {
            var known = new HashSet<int>
            {
                Push, Pull, StepBack, StepForward,
                AddState, RemoveState,
                CastSpell, TriggerSpell, NearestTargetExecuteSpell,
                RemoveSpellEffects, ChangeLook,
                Summon, FireHeal, HealPercent, Kill,
            };

            for (int damage = FirstDamage; damage <= LastDamage; damage++) known.Add(damage);
            return known;
        }

        /// <summary>
        /// What the engine will do with one effect, given its row in the catalogue.
        /// </summary>
        /// <param name="effectId">The effect.</param>
        /// <param name="characteristic">Its <c>Effects.Characteristic</c>, or zero.</param>
        /// <param name="category">Its <c>Effects.Category</c>.</param>
        public static EffectSupportKind Classify(int effectId, int characteristic, int category)
        {
            if (HandledDirectly.Contains(effectId)) return EffectSupportKind.Direct;

            // The two conditions the engine's own catalogue query applies, in the same order.
            if (characteristic > 0 && category != WeaponCategory) return EffectSupportKind.Characteristic;

            return EffectSupportKind.PanelOnly;
        }
    }
}
