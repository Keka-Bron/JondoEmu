using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

/// <summary>Current server travel catalogue and calendar, with injectable sources for entry.</summary>
internal sealed class EntryEnvironmentMessages(Func<long, IEnumerable<long>> destinations, Func<int?> dateId,
                                               Action<string> log, Action<long, long>? recordDiscovery = null)
{
    internal static EntryEnvironmentMessages Live { get; } = new(ZaapDiscoveries.KnownMaps,
        () => Almanax.Today()?.Id, Console.WriteLine, (id, map) => ZaapDiscoveries.Discover(id, map));

    // Each hjk replaces the client's known list. Always send the complete character snapshot.
    internal ZaapKnownListEvent Zaaps(long characterId) => new(destinations(characterId).ToArray());

    internal AlmanachCalendarDateEvent? Calendar()
    {
        if (dateId() is int id) return new(id);
        log("[World] Almanax calendar unavailable for the current day; calendar event omitted.");
        return null;
    }

    internal Task SendKnownAsync(GameSession session) => session.SendAsync(Zaaps(session.CharacterId));

    internal Task RecordArrivalAsync(GameSession session, long mapId)
    {
        recordDiscovery?.Invoke(session.CharacterId, mapId);
        return SendKnownAsync(session);
    }

    internal static Task SendDiscoveredAsync(GameSession session, long mapId) => Live.RecordArrivalAsync(session, mapId);
}
