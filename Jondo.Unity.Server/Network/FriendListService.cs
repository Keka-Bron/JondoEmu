using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal interface IFriendListBackend
{
    IReadOnlyList<long> Contacts(long accountId);
    bool IsContact(long accountId, long otherId);
    GameSession? Playing(long accountId);
    AccountTag Tag(long accountId);
    long HoursOffline(long accountId);
    bool SharesHavenBag(long characterId);
}

internal sealed class FriendListService(IFriendListBackend backend)
{
    internal static FriendListService Live { get; } = new(new LiveBackend());

    internal FriendListEvent Mutual(long accountId)
    {
        var friends = new List<FriendInformation>();
        foreach (long other in backend.Contacts(accountId).ToArray())
        {
            if (!backend.IsContact(other, accountId)) continue;
            var playing = backend.Playing(other);
            var state = playing?.State;
            FriendOnlineInformation? online = state == null ? null : new(
                Status: SocialStatusMessages.Status(state), LadderPosition: 0, Alliance: null,
                CharacterLevel: Math.Max(1, state.CharacterLevel), Guild: null, AchievementPoints: 0,
                BreedId: state.Breed, MoodSmileyId: 0, CharacterId: state.CharacterId,
                HavenBagShared: backend.SharesHavenBag(state.CharacterId),
                Gender: SocialStatusMessages.Gender(state.Sex), CharacterName: state.CharacterName ?? "");
            friends.Add(new(other, -1, state == null ? backend.HoursOffline(other) : 0, online,
                state == null ? CharacterState.NotConnected : state.IsInFight ? CharacterState.InFight : CharacterState.InRolePlay,
                backend.Tag(other)));
        }
        return new(friends.ToArray());
    }

    internal AcquaintanceListEvent Contacts(long accountId)
        => new(backend.Contacts(accountId).ToArray().Where(other => !backend.IsContact(other, accountId))
            .Select(other => new AcquaintanceInformation(null, other, backend.Tag(other), CharacterState.UnknownState)).ToArray());

    internal Task SendMutualAsync(GameSession owner) => owner.SendAsync(Mutual(owner.AccountId));
    internal Task SendContactsAsync(GameSession owner) => owner.SendAsync(Contacts(owner.AccountId));

    internal async Task RefreshAsync(GameSession owner)
    {
        await SendMutualAsync(owner);
        await SendContactsAsync(owner);
    }

    internal async Task RefreshOfAsync(long accountId)
    {
        var recipient = backend.Playing(accountId);
        if (recipient == null) return;
        try { await RefreshAsync(recipient); }
        catch (Exception ex) { Console.WriteLine($"[Friends] Could not refresh the lists of {accountId}: {ex.Message}"); }
    }

    internal async Task AnnouncePresenceAsync(long accountId)
    {
        if (accountId <= 0) return;
        foreach (long other in backend.Contacts(accountId).ToArray())
        {
            if (!backend.IsContact(other, accountId)) continue;
            var recipient = backend.Playing(other);
            if (recipient == null) continue;
            try { await SendMutualAsync(recipient); }
            catch (Exception ex) { Console.WriteLine($"[Friends] Could not tell {other} about {accountId}: {ex.Message}"); }
        }
    }

    private sealed class LiveBackend : IFriendListBackend
    {
        public IReadOnlyList<long> Contacts(long id) => Managers.Contacts.ContactsOf(id);
        public bool IsContact(long id, long other) => Managers.Contacts.IsContact(id, other);
        public GameSession? Playing(long id) => SessionRegistry.InWorld().FirstOrDefault(s => s.AccountId == id);
        public AccountTag Tag(long id)
        {
            var (name, tag) = HouseHandler.AccountTagOf(id);
            return new(name, tag);
        }
        public bool SharesHavenBag(long id) => (HavenBagStore.SharedOf(id) & HavenBagStore.SharedWithFriends) != 0;
        public long HoursOffline(long id)
        {
            DateTimeOffset? last = null;
            foreach (var character in DatabaseManager.GetCharactersByAccountId(id))
            {
                if (!DateTimeOffset.TryParse(character.LastConnection, out var when)) continue;
                if (last == null || when > last) last = when;
            }
            return last == null ? 0 : Math.Max(0, (long)(DateTimeOffset.Now - last.Value).TotalHours);
        }
    }
}
