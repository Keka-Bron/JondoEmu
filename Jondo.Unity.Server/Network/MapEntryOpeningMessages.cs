using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

/// <summary>The map entry opening, captured from its owner before the first send.</summary>
internal sealed class MapEntryOpeningMessages(
    Func<long, bool> hasGuild,
    Func<long, int> contributionsLeft,
    Func<long, IReadOnlyList<long>> contacts,
    Func<long, long, bool> isContact,
    Func<IReadOnlyList<long>> onlineAccounts)
{
    internal static MapEntryOpeningMessages Live { get; } = new(
        characterId => GuildStore.GuildOf(characterId) != null,
        GuildStore.ContributionsLeft, Contacts.ContactsOf, Contacts.IsContact,
        () => SessionRegistry.InWorld().Select(session => session.AccountId).ToArray());

    internal IReadOnlyList<GameEvent> Snapshot(GameSession session, GameContext context)
    {
        long characterId = session.CharacterId, accountId = session.AccountId;
        int left = hasGuild(characterId) ? contributionsLeft(characterId) : 0;
        var online = onlineAccounts().ToHashSet();
        int friends = contacts(accountId).Distinct()
            .Count(other => other > 0 && other != accountId && online.Contains(other) && isContact(other, accountId));

        return [new PongEvent(Quiet: true), new GuildContributionLeftEvent(left),
            EntryInformationMessages.OnlineMutualFriends(friends), new ContextDestroyEvent(), new ContextCreationEvent(context)];
    }
}
