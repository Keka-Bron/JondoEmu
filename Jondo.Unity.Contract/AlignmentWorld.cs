using System.Data.Common;

namespace Jondo.Unity.ServerMods;

public interface IAlignmentWorld
{
    SelectedCharacter? FindSelectedCharacter(long accountId);
    WorldCharacter? FindCharacter(long characterId);
    IEnumerable<PvpCharacter> FindPvpCharacters(int side, int minimumLevel);
    CharacterPresence? FindPresence(long characterId);
    WorldLocation Locate(long mapId);
    PvpAlignment AlignmentOf(long characterId);

    void AddHonour(long characterId, int amount);
    Task RaisePvpWingsAsync(long characterId);
    Task LeaveArenaQueueAsync(long characterId);
    Task SendNoticeAsync(long characterId, string text);
    Task SendSystemMessageAsync(long characterId, int message, params string[] arguments);
}

public sealed record SelectedCharacter(long Id, string Name, int Level);
public sealed record WorldCharacter(long Id, string Name, int Level, int Breed);
public sealed record PvpCharacter(WorldCharacter Character, string LastConnection);
public sealed record CharacterPresence(string Name, string Language, bool IsInWorld, bool IsInFight, long MapId);
public readonly record struct WorldLocation(bool KnownMap, (int X, int Y)? WorldPosition);
public readonly record struct PvpAlignment(int Side, int? EnemySide, bool Pvp, int HonourGrade, int Honour, int Dishonour)
{
    public bool Sworn => EnemySide.HasValue;
    public bool PvpOn => Sworn && Pvp;
}

public interface IWorldStorage
{
    /// <summary>Opens the world database. The caller disposes the connection.</summary>
    DbConnection OpenWorldDatabase();
}

public interface ISimulatedPlayerWorld
{
    IDisposable QuietForBots(long first, long second);
}
