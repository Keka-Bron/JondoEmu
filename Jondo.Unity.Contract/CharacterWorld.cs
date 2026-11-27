namespace Jondo.Unity.ServerMods;

/// <summary>Character storage, real-session selection, party operations and map output.</summary>
public interface ICharacterWorld
{
    ICharacterSession? FindSelectedSession(long accountId);
    IReadOnlyList<CharacterSummary> CharactersOf(ICharacterSession master);
    int CharacterSlotsLeft(ICharacterSession master);
    (long Id, string Error) CreateCharacter(ICharacterSession master, string name, int breed, int sex, int head, IReadOnlyList<long> colors);
    string SuggestName();
    bool CharacterNameTaken(string name);
    bool AccountOwnsCharacter(long accountId, long characterId);
    string PreviewLook(int breed, int sex, int head);
    string? PortraitLook(long id);
    void WarmPortraits(IEnumerable<string> looks);
    byte[]? RenderPortrait(string look);
    int CharacterLevel(ICharacterSession session);
    CharacterVitals CharacterVitals(ICharacterSession session);
    CharacterSheet ReadCharacterSheet(ICharacterSession session);
}

public interface IQuestWorld
{
    bool NpcPresent(int npcId);
    int QuestProgress(ICharacterSession session, int questId);
    int QuestStep(ICharacterSession session, int questId);
    string LanguageOf(ICharacterSession session);
    Task DeliverQuestAchievementsAsync(ICharacterSession master, ICharacterSession follower);
    Task SendQuestNoticeAsync(ICharacterSession recipient, string text);
    int QuestItemCount(ICharacterSession session, int item);
    int SpareQuestItems(ICharacterSession session, int item, int keeps);
    Task<int> MoveQuestItemsAsync(ICharacterSession source, ICharacterSession destination, int item, int count);
}

public interface ICompanionWorld
{
    bool OwnsCharacter(ICharacterSession master, long characterId);
    bool IsClientOnline(long characterId);
    ICharacterSession? LoadFollower(ICharacterSession master, long characterId);
    void SaveFollower(ICharacterSession follower);
    void NotifyPresence(long characterId);
    Task SaveAndRemoveActorAsync(ICharacterSession master, bool reloadMap);
    Task<bool> SwitchCharacterAsync(ICharacterSession master, long characterId, CharacterPosition position, bool reloadMap);
}

public interface IPartyWorld
{
    int? PartyOf(long characterId);
    bool IsInParty(long characterId);
    Task AddPartyMemberAsync(ICharacterSession master, long followerId, int? partyId);
    Task RemovePartyMemberAsync(long characterId);
    (int Id, long Leader)? JoinedParty(long characterId);
    Task PromotePartyLeaderAsync(ICharacterSession session, int partyId);
    Task SendPartyAsync(ICharacterSession session, int partyId);
}

public interface IMovementWorld
{
    IReadOnlyCollection<int>? WalkableCells(long mapId);
    int CellDistance(int from, int to);
    IReadOnlyList<int> FindPath(int from, int to, HashSet<int> walkable);
    Task DrawFollowerAsync(ICharacterSession master, ICharacterSession follower);
    Task RemoveActorAsync(long mapId, long characterId);
    Task WalkFollowerAsync(ICharacterSession master, long characterId, IReadOnlyList<int> path);
    void DiscoverZaap(long characterId, long mapId);
    Task UpdateAchievementsAsync(ICharacterSession master, ICharacterSession follower);
}

