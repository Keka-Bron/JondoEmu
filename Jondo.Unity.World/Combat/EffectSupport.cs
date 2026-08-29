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

        /// <summary>The caster steps back.</summary>
        public const int StepBack = 1041;

        /// <summary>The caster steps forward.</summary>
        public const int StepForward = 1042;

        /// <summary>Put a state on somebody.</summary>
        public const int AddState = 950;

        /// <summary>Take a state off.</summary>
        public const int RemoveState = 951;

        /// <summary>Cast another spell. This is the one that makes triggers work.</summary>
        public const int CastSpell = 792;

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
                CastSpell, Summon, FireHeal, HealPercent, Kill,
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
