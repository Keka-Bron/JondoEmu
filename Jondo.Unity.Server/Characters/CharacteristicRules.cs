namespace Jondo.Unity.Server;

/// <summary>Protocol-independent characteristic bases and derived contributions.</summary>
internal static class CharacteristicRules
{
    internal const int BasePods = 1000;
    internal const int PlayerBaseAp = 6;
    internal const int PlayerBaseMp = 3;
    internal static int InnateAp(int level) => PlayerBaseAp + (level >= 100 ? 1 : 0);

    /// <summary>Omega levels continue progression without increasing base life or characteristic capital.</summary>
    internal const int MaxLevelWithStats = Jondo.Unity.World.Fights.Fighter.MaxLevelWithStats;

    internal static int StatLevel(int level) => Math.Min(level, MaxLevelWithStats);
    internal static int TotalCapitalForLevel(int level) => (Math.Max(1, StatLevel(level)) - 1) * 5;
    internal static int BaseLifeForLevel(int level) => 50 + 5 * Math.Max(1, StatLevel(level));

    /// <summary>Allocation cost, widened before multiplication or addition and never narrowed to a budget.</summary>
    internal static long ComputeDistributionCost(int strength, int intelligence, int chance,
        int agility, int vitality, int wisdom, int breed = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(strength);
        ArgumentOutOfRangeException.ThrowIfNegative(intelligence);
        ArgumentOutOfRangeException.ThrowIfNegative(chance);
        ArgumentOutOfRangeException.ThrowIfNegative(agility);
        ArgumentOutOfRangeException.ThrowIfNegative(vitality);
        ArgumentOutOfRangeException.ThrowIfNegative(wisdom);
        return checked(Managers.BreedStatCost.CostOf(breed, "strength", strength)
            + Managers.BreedStatCost.CostOf(breed, "intelligence", intelligence)
            + Managers.BreedStatCost.CostOf(breed, "chance", chance)
            + Managers.BreedStatCost.CostOf(breed, "agility", agility)
            + Managers.BreedStatCost.CostOf(breed, "vitality", vitality)
            + Managers.BreedStatCost.CostOf(breed, "wisdom", wisdom));
    }

    /// <summary>
    /// Contributions from total primary characteristics, including scrolls, equipment and
    /// alterations: a tenth of wisdom for dodge/withdrawal, a tenth of agility for escape/lock,
    /// five pods per strength, the four elements for initiative and a tenth of chance for prospecting.
    /// The owner sheet includes these in Base; presets expose them in a separate column.
    /// </summary>
    internal static long GivenByCharacteristics(int id, Func<int, long> total)
        => id switch
        {
            Stat.DodgeActionPoints or Stat.DodgeMovementPoints or Stat.WithdrawActionPoints or Stat.WithdrawMovementPoints
                => total(Stat.Wisdom) / 10,
            Stat.Escape or Stat.Lock => total(Stat.Agility) / 10,
            Stat.Pods => checked(5 * total(Stat.Strength)),
            Stat.Initiative => checked(total(Stat.Strength) + total(Stat.Intelligence) + total(Stat.Chance) + total(Stat.Agility)),
            Stat.Prospecting => total(Stat.Chance) / 10,
            _ => 0,
        };
}
