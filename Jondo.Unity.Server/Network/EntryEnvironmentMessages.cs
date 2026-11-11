using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

/// <summary>Current server travel catalogue and calendar, with injectable sources for entry.</summary>
internal sealed class EntryEnvironmentMessages(Func<IEnumerable<long>> destinations, Func<int?> dateId, Action<string> log)
{
    internal static EntryEnvironmentMessages Live { get; } = new(Interactives.DiscoveredZaapMaps,
        () => Almanax.Today()?.Id, Console.WriteLine);

    /// <summary>
    /// Known destinations make the travel window show its list: the historical 182-byte
    /// snapshot contained 45 activated zaaps. Without it the client showed «Ningún destino»
    /// even when the travel-window packet offered destinations. Jondo currently grants all
    /// activated zaaps with a usable departure element, rather than persisting discovery.
    /// </summary>
    internal ZaapKnownListEvent Zaaps() => new(destinations().ToArray());

    internal AlmanachCalendarDateEvent? Calendar()
    {
        if (dateId() is int id) return new(id);
        log("[World] Almanax calendar unavailable for the current day; calendar event omitted.");
        return null;
    }

    internal static Task SendDiscoveredAsync(GameSession session, long mapId)
        => session.SendAsync(new ZaapKnownListEvent([mapId]));
}
