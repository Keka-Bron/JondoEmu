using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

/// <summary>Current server travel catalogue and calendar, with injectable sources for entry.</summary>
internal sealed class EntryEnvironmentMessages(Func<IEnumerable<long>> destinations, Func<int?> dateId, Action<string> log,
                                               Func<long, bool>? isZaap = null)
{
    internal static EntryEnvironmentMessages Live { get; } = new(Interactives.DiscoveredZaapMaps,
        () => Almanax.Today()?.Id, Console.WriteLine, map => Interactives.WaypointOf(map) != null);

    /// <summary>
    /// Known destinations make the travel window show its list: the historical 182-byte
    /// snapshot contained 45 activated zaaps. Without it the client showed «Ningún destino»
    /// even when the travel-window packet offered destinations. Jondo currently grants all
    /// activated zaaps with a usable departure element, rather than persisting discovery.
    /// </summary>
    internal ZaapKnownListEvent Zaaps() => new(destinations().ToArray());

    /// <summary>
    /// The known zaaps again on arriving on a map, the map with them when it is a zaap.
    /// </summary>
    /// <remarks>
    /// The client REPLACES its known list with each hjk (ees.dtd keeps the message's list, after
    /// telling which ones are new), so a list of the arrival map alone left it knowing that one
    /// map: after the first step every zaap of the world map was a «?», while the minimap,
    /// which does not ask, still named them.
    /// </remarks>
    internal ZaapKnownListEvent Zaaps(long arrivedOn)
    {
        var known = destinations().ToList();
        if (isZaap?.Invoke(arrivedOn) == true && !known.Contains(arrivedOn)) known.Add(arrivedOn);
        return new(known.ToArray());
    }

    internal AlmanachCalendarDateEvent? Calendar()
    {
        if (dateId() is int id) return new(id);
        log("[World] Almanax calendar unavailable for the current day; calendar event omitted.");
        return null;
    }

    internal Task SendKnownAsync(GameSession session, long mapId) => session.SendAsync(Zaaps(mapId));

    internal static Task SendDiscoveredAsync(GameSession session, long mapId) => Live.SendKnownAsync(session, mapId);
}
