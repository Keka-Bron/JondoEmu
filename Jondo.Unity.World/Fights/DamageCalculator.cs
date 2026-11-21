using System;

namespace Jondo.Unity.World.Fights
{
    public enum ElementType
    {
        Neutral = 0,
        Earth = 1,
        Fire = 2,
        Water = 3,
        Air = 4
    }

    /// <summary>
    /// A damage blow up to the target's resistances, in the order and with the roundings of the
    /// client's own fight preview (3.6.10.11, read with Ghidra over the REA IL2CPP seeds; the
    /// names are obfuscated, the characteristics' are the client's StatId enum's).
    /// </summary>
    /// <remarks>
    /// <para>hal.bncy adds the element's characteristic, 25 (DamagePercent, power) and 98
    /// (DamagePercentSpell) or 103 (WeaponPower), and multiplies the roll by the sum when it is
    /// above zero (ra.pqh); hal.bncz then adds the flat bonuses -- 16, the element's 88 to 92 and
    /// 86 on a critical --; hal.bnde multiplies by 31 (WeaponDamagePercent) when there is any.
    /// ham.bndh takes the target's element reduction (54 to 58) and, on a critical, its 87
    /// (CriticalDamageReduction) off it, and multiplies what is left by <c>1 - resistance × 0.01</c>
    /// in single precision, never below zero.</para>
    /// <para>Every multiplication rounds DOWN at once (ra.fay, ra.ppy, ra.pqh: a floor of the
    /// product taken as a float), not to the nearest at the end: with 47 % resistance a blow of
    /// 100 leaves 52, as 1 - 0.01f × 47 is a hair under 0.53.</para>
    /// </remarks>
    public static class DamageCalculator
    {
        public static int CalculateDamage(
            int baseDamage,
            ElementType element,
            int statValue,           // E.g. Strength for Earth, Intelligence for Fire
            int power,               // General Power
            int flatElementDamage,   // Flat elemental damage
            int flatDamage,          // General flat damage
            int targetResPct,        // Target resistance %
            int targetFlatRes,       // Target flat resistance: its element reduction, and its 87 on a critical
            bool isCritical = false,
            double critBonusMultiplier = 1.25,
            int percentBonus = 0,         // 98 for a spell's blow, 103 for a weapon's
            int weaponDamagePercent = 0)  // 31
        {
            if (baseDamage <= 0) return 0;

            int effectiveBase = isCritical ? (int)Math.Round(baseDamage * critBonusMultiplier) : baseDamage;

            // 1. The characteristics, power and the % bonus together, when they add up to more than nothing.
            int percent = statValue + power + percentBonus;
            int damage = percent > 0 ? TimesPercent(effectiveBase, 100 + percent) : effectiveBase;

            // 2. The flat ones on top, and the weapon damage percent on all of it.
            damage += flatElementDamage + flatDamage;
            if (weaponDamagePercent != 0) damage = TimesPercent(damage, 100 + weaponDamagePercent);

            // 3. The target's flat reduction, then its percentage resistance.
            damage -= targetFlatRes;
            float left = Math.Max(0f, 1f - targetResPct * 0.01f);
            damage = (int)MathF.Floor(damage * left);

            return Math.Max(1, damage);
        }

        /// <summary>
        /// A value times a percentage the client's way: <c>floor((float)(value × percent × 0.01))</c>
        /// (ra.fay, ra.pqh).
        /// </summary>
        public static int TimesPercent(int value, int percent)
            => (int)Math.Floor((float)(value * (percent * 0.01)));
    }
}
