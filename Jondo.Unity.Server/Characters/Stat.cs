namespace Jondo.Unity.Server;

/// <summary>Shared characteristic IDs used by gameplay calculations and message builders.</summary>
public static class Stat
{
    public const int LifePoints = 0;
    public const int ActionPoints = 1;
    /// <summary>Points still to be spent. Proved by the capture: 995 before spending
    /// fifteen on the sheet, 980 after.</summary>
    public const int RemainingPoints = 3;
    public const int Strength = 10;
    public const int Vitality = 11;
    public const int Wisdom = 12;
    public const int Chance = 13;
    public const int Agility = 14;
    public const int Intelligence = 15;
    public const int Critical = 18;
    public const int Range = 19;
    public const int MovementPoints = 23;
    public const int Power = 25;
    public const int Summons = 26;
    public const int DodgeActionPoints = 27;
    public const int DodgeMovementPoints = 28;
    public const int Pods = 40;
    public const int Initiative = 44;

    /// <summary>
    /// The energy, "energyPoints" in the client's catalogue, and the gauge it fills,
    /// "maxEnergyPoints". 47 was named Energy here and carried the 10,000 of the gauge,
    /// while 29 went out at zero: every captured kub has 10000 in both, or 8000 in the 29
    /// once a fight is lost. See <see cref="Managers.Energy"/>.
    /// </summary>
    public const int EnergyPoints = 29;
    public const int MaxEnergyPoints = 47;

    /// <summary>
    /// "hitPointLoss": the life missing outside a fight, negative, in the f2 of its f4.
    /// "f4 { f2: -576 }" after the defeats of the captures. See <see cref="Managers.RestingLife"/>.
    /// </summary>
    public const int HitPointLoss = 97;
    public const int Prospecting = 48;
    public const int Heals = 49;
    public const int Escape = 78;
    public const int Lock = 79;
    public const int WithdrawActionPoints = 82;
    public const int WithdrawMovementPoints = 83;
    public const int Shield = 96;
}
