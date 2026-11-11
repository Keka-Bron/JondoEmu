using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal interface IGameEntryMapBackend
{
    Task SendMapAsync(GameSession session, DatabaseManager.DbCharacter character);
    Task SendWardrobeAsync(GameSession session);
    Task SendHousesAsync(GameSession session);
    Task SendJournalAsync(GameSession session);
    Task SendAchievementsAsync(GameSession session);
    Task SendPresetsAsync(GameSession session);
    Task SendMarksAsync(GameSession session);
}

/// <summary>The existing map-entry hook sequence, with an explicit character-owned emote snapshot.</summary>
internal sealed class GameEntryMapInitialization(IGameEntryMapBackend backend)
{
    internal async Task SendAsync(GameSession session, DatabaseManager.DbCharacter character)
    {
        using var context = SessionContext.Push(session);
        await backend.SendMapAsync(session, character);
        // The capture's houses, journal, emotes, achievements and presets no longer
        // travel; these are the current account's and character's state.
        await backend.SendWardrobeAsync(session);
        await backend.SendHousesAsync(session);
        await backend.SendJournalAsync(session);
        await Emotes.SendListAsync(session);
        await backend.SendAchievementsAsync(session);
        await backend.SendPresetsAsync(session);
        await backend.SendMarksAsync(session);
    }
}
