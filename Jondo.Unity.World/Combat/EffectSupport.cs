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
    ///                    generically without knowing what it is. 213 of 872 are like this.
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
        /// Summon a controllable creature. Osamodas' two Gobgob spells use this id instead of the
        /// ordinary summon id; the creature still has to be created by the same fight code.
        /// </summary>
        public const int ControllableSummon = 1011;

        /// <summary>Set the cooldown of the spell carried in DiceNum to Value rounds.</summary>
        public const int SetCooldown = 1045;

        /// <summary>Heal a fixed amount, scaled by Intelligence and the Heals characteristic.</summary>
        public const int Heal = 108;

        /// <summary>Elemental heals scale with Chance, Agility and Strength respectively.</summary>
        public const int WaterHeal = 2998;
        public const int AirHeal = 2999;
        public const int EarthHeal = 3000;

        /// <summary>Heal a percentage of maximum life.</summary>
        public const int HealPercent = 1109;

        /// <summary>Kill outright.</summary>
        public const int Kill = 141;

        /// <summary>Damage effects run from 91 to 100 and carry their element in the catalogue.</summary>
        public const int FirstDamage = 91;

        public const int LastDamage = 100;

        /// <summary>Neutral damage equal to a percentage of the caster's current/missing health.</summary>
        public const int CasterCurrentHealthDamage = 89;
        public const int CasterMissingHealthDamage = 279;

        /// <summary>Damage in whichever elemental characteristic is highest on the caster.</summary>
        public const int BestElementDamage = 2822;

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
                CastSpell, Summon, ControllableSummon, SetCooldown,
                Heal, WaterHeal, AirHeal, EarthHeal, HealPercent, Kill,
                CasterCurrentHealthDamage, CasterMissingHealthDamage, BestElementDamage,
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
