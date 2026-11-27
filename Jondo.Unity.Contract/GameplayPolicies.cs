using System.Text.Json;

namespace Jondo.Unity.ServerMods;

public enum PlayerActivity { Fight, Koliseo, Merkasako, Dungeon, House }
public enum AutomaticEncounter { MonsterAggression, TurnOffPvp }
public readonly record struct PvpCombatant(long Id, int Level);

public interface IPlayerActivityPolicy
{
    bool BlocksAutomaticEncounter(long characterId, AutomaticEncounter encounter);
    Task<bool> BlocksActivityAsync(long characterId, PlayerActivity activity, string language, Func<string, Task>? notify);
    Task<bool> BlocksDuelAsync(long characterId, long otherId, string language, Func<string, Task> notify);
}

public interface IPvpEvents
{
    void AlignmentChanged(long characterId);
    void CharacterDeleted(long characterId);
    Task AggressionEndedAsync(IReadOnlyList<PvpCombatant> winners, IReadOnlyList<PvpCombatant> losers);
}

public interface IDialogueCurrency
{
    int Cost(IReadOnlyDictionary<string, JsonElement> fields);
    bool AdjustBalance(long characterId, int amount);
}

public interface ISimulatedPlayerEncounters
{
    Task<bool> TryFindEncounterAsync(long characterId, int level, bool inWorld, Func<int, int> choose);
    long? EncounterTarget(long characterId);
}
