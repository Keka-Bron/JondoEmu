namespace Jondo.Unity.ServerMods;

public readonly record struct ControlledFightParticipant(ICharacterSession Session, long ControllerId);

/// <summary>Optional policy for characters who participate through another player's client.</summary>
public interface IFightParticipantPolicy
{
    /// <summary>Select ordered participants within the available places before the fight is committed.</summary>
    IReadOnlyList<ControlledFightParticipant> SelectOpeningParticipants(long openerId, int places);

    /// <summary>Resolve the active session the host can use for reward planning and settlement.</summary>
    ControlledFightParticipant? FindParticipant(long characterId);

    bool ShouldPassTurn(long characterId);
    IReadOnlyList<(int Slot, int Spell)> ArrangeControlledBar(int weaponSpell, bool withWeapon,
        IReadOnlyList<(int Slot, int Spell)> bar, IReadOnlyList<(int Spell, int Grade)> spells);

    /// <summary>Choose transfers of shared loot between winners before the host applies rewards.</summary>
    IReadOnlyList<(long Giver, long Recipient)> SelectLootRecipients(IReadOnlyList<long> winners);

    Task RestoreFollowerPresenceAsync(ICharacterSession controller);
}
