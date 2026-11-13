using System.Collections.Frozen;

namespace Jondo.Unity.Server.Network;

internal enum CharacteristicKind { Detailed, Usable, Value }

internal sealed record CharacteristicDefinition(int Id, CharacteristicKind Kind = CharacteristicKind.Detailed, long BaseValue = 0);

/// <summary>Immutable characteristic order, representations and fixed base values.</summary>
internal sealed class CharacteristicCatalogue
{
    // Preserve the complete 120-entry sheet order. IDs without established names remain
    // numeric; a zero base is intentional, while percentage multipliers start at 100.
    // State-dependent values and derived contributions are calculated by CharacteristicsMessages.
    internal static CharacteristicCatalogue Default { get; } = new([
        new(Stat.LifePoints),
        new(Stat.ActionPoints, CharacteristicKind.Usable),
        new(Stat.RemainingPoints),
        new(5),
        new(Stat.Strength),
        new(Stat.Vitality),
        new(Stat.Wisdom),
        new(Stat.Chance),
        new(Stat.Agility),
        new(Stat.Intelligence),
        new(16),
        new(17),
        new(Stat.Critical),
        new(Stat.Range),
        new(20),
        new(21),
        new(22),
        new(Stat.MovementPoints, CharacteristicKind.Usable, 3),
        new(24),
        new(Stat.Power),
        new(Stat.Summons, BaseValue: Handlers.FightHandler.BasePlayerSummonLimit),
        new(Stat.DodgeActionPoints),
        new(Stat.DodgeMovementPoints),
        new(Stat.EnergyPoints, CharacteristicKind.Value),
        new(30),
        new(31),
        new(32),
        new(33),
        new(34),
        new(35),
        new(36),
        new(37),
        new(39),
        new(Stat.Pods, BaseValue: CharacteristicRules.BasePods),
        new(41),
        new(42),
        new(43),
        new(Stat.Initiative),
        new(45),
        new(46),
        new(Stat.MaxEnergyPoints, CharacteristicKind.Value, Jondo.Unity.World.Fights.DefeatPenalty.MaxEnergy),
        new(Stat.Prospecting, BaseValue: 100),
        new(Stat.Heals),
        new(50),
        new(51),
        new(52),
        new(53),
        new(54),
        new(55),
        new(56),
        new(57),
        new(58),
        new(69),
        new(70),
        new(71),
        new(72),
        new(74),
        new(75, BaseValue: 10),
        new(76),
        new(77),
        new(Stat.Escape),
        new(Stat.Lock),
        new(80),
        new(81),
        new(Stat.WithdrawActionPoints),
        new(Stat.WithdrawMovementPoints),
        new(84),
        new(85),
        new(86),
        new(87),
        new(88),
        new(89),
        new(90),
        new(91),
        new(92),
        new(93),
        new(94),
        new(95),
        new(Stat.Shield, CharacteristicKind.Value),
        new(Stat.HitPointLoss),
        new(98),
        new(99),
        new(100),
        new(101),
        new(102),
        new(103),
        new(104),
        new(105),
        new(106),
        new(107, BaseValue: 100),
        new(108),
        new(109),
        new(110),
        new(120, BaseValue: 100),
        new(121, BaseValue: 100),
        new(122, BaseValue: 100),
        new(123, BaseValue: 100),
        new(124, BaseValue: 100),
        new(125, BaseValue: 100),
        new(126),
        new(127),
        new(128),
        new(129),
        new(130),
        new(131),
        new(132),
        new(133),
        new(134),
        new(135),
        new(136),
        new(137),
        new(138),
        new(139),
        new(140),
        new(141, BaseValue: 100),
        new(142, BaseValue: 100),
        new(143, BaseValue: 100),
        new(150, BaseValue: 100),
        new(158),
        new(200),
    ]);

    private readonly FrozenDictionary<int, CharacteristicDefinition> definitions;
    internal IReadOnlyList<int> Ids { get; }

    internal CharacteristicCatalogue(IEnumerable<CharacteristicDefinition> rows)
    {
        var entries = rows.ToArray();
        definitions = entries.ToDictionary(entry => entry.Id).ToFrozenDictionary();
        Ids = Array.AsReadOnly(entries.Select(entry => entry.Id).ToArray());
    }

    // Legacy fighter sheets can contain IDs outside the owner sheet; retain their
    // detailed representation without adding them to the declared owner-sheet order.
    internal CharacteristicKind KindOf(int id) => definitions.GetValueOrDefault(id)?.Kind ?? CharacteristicKind.Detailed;
    internal long BaseValueOf(int id) => definitions.GetValueOrDefault(id)?.BaseValue ?? 0;
}
