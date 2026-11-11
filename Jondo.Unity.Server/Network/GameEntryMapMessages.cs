using Jondo.Unity.Protocol.Game;

namespace Jondo.Unity.Server.Network;

/// <summary>Current map state, spell book, shortcut snapshots and channel policy.</summary>
internal sealed class GameEntryMapMessages(TimeProvider clock, Func<int> regenerationRate, Func<GameSession, SpellsEvent> spells,
    Func<GameSession, (ShortcutBarContentEvent Spell, ShortcutBarContentEvent General)> shortcuts)
{
    internal static GameEntryMapMessages Live { get; } = new(TimeProvider.System, ConnectionProtocol.RegenerationRateNow,
        SpellMessages.Live.Snapshot, ShortcutBarMessages.Live.Snapshot);

    internal async Task<int> SendAsync(GameSession session, MapEntryOpeningMessages opening, GameContext context,
        long mapId, Func<Task<int>>? afterTime = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mapId);
        var messages = opening.Snapshot(session, context);
        int sent = 0;
        foreach (var message in messages)
            await SendAsync(message);

        if (context == GameContext.RolePlay)
        {
            await SendAsync(new LifePointsRegenBeginEvent(regenerationRate()));
            // Early reset only. The existing quest hooks send current marks later.
            await SendAsync(new NpcsMapQuestStatusUpdateEvent([]));
        }
        await SendAsync(new MapCurrentEvent(mapId));
        await SendAsync(ServerTimeMessages.Snapshot(clock));
        if (afterTime != null) sent += await afterTime();

        await SendAsync(spells(session));
        var bars = shortcuts(session);
        await SendAsync(bars.Spell);
        await SendAsync(bars.General);

        // Only the message identity is reviewed; the captured scalar has no semantic binding.
        await SendAsync(new CommunityChannelEvent());
        foreach (var update in ChatChannelMessages.Updates(session.State))
            await SendAsync(update);
        // No local BAK API key issuer: explicitly advertise no key.
        await SendAsync(new BakApiKeyEvent(""));

        if (context == GameContext.RolePlay)
            session.State.RegenerationStartedUtc = clock.GetUtcNow().UtcDateTime;
        return sent;

        async Task SendAsync(GameEvent message)
        {
            await session.SendAsync(message);
            sent++;
        }
    }
}
