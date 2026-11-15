using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server;

/// <summary>A detached human combat calculation captured from one explicit session.
/// Incarnations, mutations and fight-only modifiers are applied by the fight pipeline afterwards.</summary>
public sealed class CombatCharacteristics
{
    private readonly int level, strength, intelligence, chance, agility, vitality;
    private readonly IReadOnlyDictionary<int, long> equipment, alterations;
    private readonly int apCap, mpCap;

    internal CombatCharacteristics(GameSession session)
    {
        var state = session.State;
        level = state.CharacterLevel;
        strength = state.TotalStrength; intelligence = state.TotalIntelligence;
        chance = state.TotalChance; agility = state.TotalAgility; vitality = state.TotalVitality;
        equipment = Equipment.BonusesOf(state.EquipmentItems.Values.Select(item => (item.Position, item)));
        alterations = new Dictionary<int, long>(Alterations.Bonuses(state.CharacterId));
        apCap = ServerSettings.Current.MaxActionPoints;
        mpCap = ServerSettings.Current.MaxMovementPoints;
    }

    public int GetEquipBonus(int statId)
        => checked((int)(equipment.GetValueOrDefault(statId) + alterations.GetValueOrDefault(statId)));

    public int GetPlayerMaxHp()
        => checked(CharacteristicRules.BaseLifeForLevel(level) + vitality + GetEquipBonus(Stat.Vitality));

    public int PlayerInnateAp() => CharacteristicRules.InnateAp(level);
    public int GetPlayerMaxAp()
        => ServerSettings.Capped(checked(PlayerInnateAp() + GetEquipBonus(Stat.ActionPoints)), apCap);
    public int GetPlayerMaxMp()
        => ServerSettings.Capped(checked(CharacteristicRules.PlayerBaseMp + GetEquipBonus(Stat.MovementPoints)), mpCap);

    public int IniciativaInvertida() => checked(strength + intelligence + chance + agility);
    public int IniciativaDelEquipo()
        => checked(GetEquipBonus(Stat.Strength) + GetEquipBonus(Stat.Intelligence) + GetEquipBonus(Stat.Chance)
            + GetEquipBonus(Stat.Agility) + GetEquipBonus(Stat.Initiative));
    public int GetPlayerInitiative() => checked(IniciativaInvertida() + IniciativaDelEquipo());
}
