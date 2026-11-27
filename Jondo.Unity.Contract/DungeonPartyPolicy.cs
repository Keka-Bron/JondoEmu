namespace Jondo.Unity.ServerMods;

/// <summary>Selects accompanying party members and proposes payment before the host commits dungeon entry.</summary>
public interface IDungeonPartyPolicy
{
    IReadOnlyList<ICompanion> DungeonFollowers(ICharacterSession master);
    DungeonAdmissionPlan PlanDungeonAdmission(DungeonAdmissionRequest request);
    bool ContainsFollower(ICompanion follower);
    bool CanResumeDungeon(IReadOnlyList<ICompanion> followers, long room, Func<long, long> savedRoom);
    Task MoveDungeonFollowersAsync(ICharacterSession master, long mapLeft, IReadOnlyList<ICompanion> admitted);
}

public readonly record struct DungeonFollowerOptions(long CharacterId, int Keys, bool HasKeyring, bool WantsKey);

/// <summary>The leader's key count excludes the item already reserved for their own entry.</summary>
public sealed record DungeonAdmissionRequest(long LeaderId, int LeaderKeys, bool RequiresKey,
    IReadOnlyList<DungeonFollowerOptions> Followers);

public enum DungeonFollowerEntry { Free, Keyring, Key }

public readonly record struct DungeonFollowerAdmission(long CharacterId, DungeonFollowerEntry Entry, long PayerId = 0);

/// <summary>A positive shortfall rejects the entire plan.</summary>
public sealed record DungeonAdmissionPlan(IReadOnlyList<DungeonFollowerAdmission> Followers, int MissingKeys = 0);
