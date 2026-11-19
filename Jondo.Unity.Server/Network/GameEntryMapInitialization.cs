using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal interface IGameEntryMapBackend
{
    Task SendMapAsync(GameSession session, DbCharacter character);
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
    internal async Task SendAsync(GameSession session, DbCharacter character)
    {
        using var context = SessionContext.Push(session);
        await backend.SendMapAsync(session, character);
        // The capture's houses, journal, emotes, achievements and presets no longer
        // travel; these are the current account's and character's state.
        // The emotes go before the wardrobe's cosmetic collection (lwt), as the official
        // entry sends them: the client builds the appearance window's aura entries from the
        // emotes it already knows when the collection arrives, and an aura told after it
        // never shows in that tab (CosmeticAuraView; only a khi adds one later).
        await Emotes.SendListAsync(session);
        await backend.SendWardrobeAsync(session);
        await backend.SendHousesAsync(session);
        await backend.SendJournalAsync(session);
        await backend.SendAchievementsAsync(session);
        await backend.SendPresetsAsync(session);
        await backend.SendMarksAsync(session);
    }
}
