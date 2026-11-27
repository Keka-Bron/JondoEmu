namespace Jondo.Unity.ServerMods;

/// <summary>The host's live session. Normal character state remains in the host.</summary>
public interface ICharacterSession
{
    long CharacterId { get; }
    long AccountId { get; }
    string CharacterName { get; }
    bool IsInWorld { get; }
    bool IsInFight { get; }
    bool HasClient { get; }
    CharacterPosition Position { get; set; }
    Task<IDisposable> EnterTurnAsync();
}

public readonly record struct CharacterPosition(long MapId, int CellId, int Orientation);

public interface ICompanion
{
    long CharacterId { get; }
    long ControllerId { get; }
    ICharacterSession Session { get; }
}

public interface ICompanionSessionPolicy
{
    IReadOnlyList<ICompanion> Of(long controllerId);
    ICompanion? Find(long characterId);
    void MasterLeft(ICharacterSession controller);
    bool SelectCharacter(long characterId, Func<bool> load);
    bool SwitchPending(long accountId);
    long TakeSwitch(long accountId);
    void Regroup(ICharacterSession controller);
}

public interface IPartyMembershipPolicy
{
    long PartyInviteRecipient(string characterName);
    bool OwnsParty(long characterId, IReadOnlyList<long> members);
    bool ReleaseKickedFollower(long leaderId, long memberId);
    bool CanLeadParty(long characterId);
    long ChoosePartyLeader(IReadOnlyList<long> members);
    bool HasPartyLeader(IReadOnlyList<long> members);
    Task LeavePartyAsync(int partyId, long controllerId);
    Task JoinPartyAsync(ICharacterSession controller);
}

public interface ITradeRoutingPolicy
{
    (long Target, ICharacterSession? AutoAcceptingSession) ResolveTradeTarget(long requester, long target);
}

public interface ICompanionMovement
{
    IEnumerable<ICompanion> VisibleFollowers(ICharacterSession controller, long mapId);
    Task MasterMovedAsync(ICharacterSession controller, long mapLeft);
    Task MasterWalkedAsync(ICharacterSession controller, IReadOnlyList<int> cells);
}

public interface IQuestSharingPolicy
{
    bool CanShareNpc(int npcId);
    bool CanShareQuestReply(int npcId, QuestReplyEffects effects, Func<bool> isAvailable);
    Task ShareQuestActionAsync(ICharacterSession controller, Func<ICharacterSession, Task> apply,
        Func<ICompanion, bool>? participates, IReadOnlyCollection<int>? quests, Func<ICharacterSession, LagCause?>? explain);
    Task<bool> PoolQuestItemsAsync(ICharacterSession controller, IEnumerable<(int Item, int Count)> asked,
        Func<ICharacterSession, bool> follows);
}
