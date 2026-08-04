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

    public static class DamageCalculator
    {
        public static int CalculateDamage(
            int baseDamage,
            ElementType element,
            int statValue,           // E.g. Strength for Earth, Intelligence for Fire
            int power,               // General Power / Potencia
            int flatElementDamage,   // Daños elementales fijos
            int flatDamage,          // Daños fijos generales
            int targetResPct,        // Resistencias % del objetivo
            int targetFlatRes,       // Resistencias fijas del objetivo
            bool isCritical = false,
            double critBonusMultiplier = 1.25)
        {
            if (baseDamage <= 0) return 0;

            int effectiveBase = isCritical ? (int)Math.Round(baseDamage * critBonusMultiplier) : baseDamage;

            // 1. Raw elemental calculation: Base * (100 + Stat + Power) / 100 + FlatElem + FlatDamage
            double multiplier = (100.0 + Math.Max(0, statValue) + Math.Max(0, power)) / 100.0;
            double rawDamage = (effectiveBase * multiplier) + flatElementDamage + flatDamage;

            // 2. Subtract flat resistance
            double afterFlatRes = Math.Max(0, rawDamage - targetFlatRes);

            // 3. Apply percentage resistance reduction
            double finalDamage = afterFlatRes * (1.0 - (targetResPct / 100.0));

            return Math.Max(1, (int)Math.Round(finalDamage));
        }
    }
}
