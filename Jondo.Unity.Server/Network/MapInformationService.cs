using System.Net.Sockets;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Handlers;

namespace Jondo.Unity.Server.Network;

internal interface IMapInformationBackend
{
    void ForgetInteractions(GameSession session);
    Task RestoreDreamAsync(GameSession session);
    void PrepareAnomaly(long mapId);
    Task SendMarksAsync(GameSession session);
    Task SendActorsAsync(GameSession session, DbCharacter character);
    Task SendFightCountAsync(GameSession session);
    bool IsHavenBag(long mapId);
    Task SendFurnitureAsync(GameSession session);
    Task RestoreRaidStagingsAsync(GameSession session);
    Task RestoreTravelersTowerAsync(GameSession session);
    Task RestoreSeasonAsync(GameSession session);
    Task RestoreAnomalyAsync(GameSession session);
    Task RestoreGargandiasAsync(GameSession session);
    Task RestoreGuildHallAsync(GameSession session);
    int SubAreaOf(long mapId);
    Task EnterQuestMapAsync(GameSession session, int subAreaId);
    Task EnterAchievementMapAsync(GameSession session, int subAreaId);
    Task EnterConquestMapAsync(GameSession session, int subAreaId);
    Task EnterLingeringRoomAsync(GameSession session);
    Task RestorePhantomAsync(GameSession session);
    Task ShowMapStagingAsync(GameSession session);
    Task PlacePendingBonusItemsAsync(GameSession session);
}

/// <summary>
/// Repeatable roleplay-map information, under the caller's session turn. Entry phase
/// guards and fight readiness routing belong to GameEntryService, before this operation.
/// </summary>
internal sealed class MapInformationService(IMapInformationBackend backend)
{
    internal static MapInformationService Live { get; } = new(new LiveBackend());

    internal async Task SendAsync(GameSession session, DbCharacter character)
    {
        using var context = SessionContext.Push(session);
        backend.ForgetInteractions(session);
        await backend.RestoreDreamAsync(session);
        backend.PrepareAnomaly(session.MapId);

        // Marks must precede actors, including an empty replacement. Sending them
        // after the actor list leaves the client's map quest display incomplete.
        await backend.SendMarksAsync(session);
        await backend.SendActorsAsync(session, character);
        await backend.SendFightCountAsync(session);

        // Furniture and permissions belong between the actors and the modifier
        // event. The modifier is a semantic event, not an actors-complete message.
        if (backend.IsHavenBag(session.MapId))
            await backend.SendFurnitureAsync(session);
        await session.SendAsync(new AreaFightModifierUpdateEvent([]));

        // Map loads reset these displays. Keep their restoration in the same order,
        // even when a hook (Gargandias) starts a fight during the map reply.
        await backend.RestoreRaidStagingsAsync(session);
        await backend.RestoreTravelersTowerAsync(session);
        await backend.RestoreSeasonAsync(session);
        await backend.RestoreAnomalyAsync(session);
        await backend.RestoreGargandiasAsync(session);
        await backend.RestoreGuildHallAsync(session);

        // These objectives belong to every map-information reply, not just the
        // once-per-world-entry initialization or the legacy map-load request.
        int subAreaId = backend.SubAreaOf(session.MapId);
        await backend.EnterQuestMapAsync(session, subAreaId);
        await backend.EnterAchievementMapAsync(session, subAreaId);
        await backend.EnterConquestMapAsync(session, subAreaId);
        await backend.EnterLingeringRoomAsync(session);
        await backend.RestorePhantomAsync(session);
        await backend.ShowMapStagingAsync(session);
        await backend.PlacePendingBonusItemsAsync(session);
        Console.WriteLine($"[Entry] Actors of map {session.MapId} sent: {character.Name} on cell {session.State.CellId}.");
    }

    /// <summary>
    /// Redraw current contents without replaying arrival hooks, marks, fight counts
    /// or furniture. Quest/admin refresh callers can use this independently of entry.
    /// </summary>
    internal async Task RefreshActorsAsync(GameSession session, DbCharacter character)
    {
        using var context = SessionContext.Push(session);
        await backend.SendActorsAsync(session, character);
        await session.SendAsync(new AreaFightModifierUpdateEvent([]));
    }

    private sealed class LiveBackend : IMapInformationBackend
    {
        public void ForgetInteractions(GameSession session)
        {
            _ = StreamOf(session);
            NpcHandler.Forget();
            WorkshopHandler.Forget();
            MarketplaceHandler.Forget();
        }

        public Task RestoreDreamAsync(GameSession session) => DreamHandler.OnMapLoadedAsync(StreamOf(session));
        public void PrepareAnomaly(long mapId) => AnomalyHandler.BeforeMapLoaded(mapId);
        public Task SendMarksAsync(GameSession session) => Managers.Quests.SendMarksAsync(StreamOf(session), session.MapId);

        public Task SendActorsAsync(GameSession session, DbCharacter character)
            => MapInformationMessages.Live.SendAsync(session, character);

        public Task SendFightCountAsync(GameSession session) => FightHandler.SendFightCountAsync(StreamOf(session), session.MapId);
        public bool IsHavenBag(long mapId) => Managers.Merkasako.IsHavenBag(mapId);
        public Task SendFurnitureAsync(GameSession session) => MerkasakoHandler.SendFurnitureAsync(StreamOf(session));
        public Task RestoreRaidStagingsAsync(GameSession session)
            => Managers.GuildRaidStagings.OnMapLoadedAsync(StreamOf(session), session.CharacterId, session.MapId);
        public Task RestoreTravelersTowerAsync(GameSession session) => TravelersTowerHandler.OnMapLoadedAsync(StreamOf(session), session.MapId);
        public Task RestoreSeasonAsync(GameSession session) => OsavoraSeasonHandler.OnMapLoadedAsync(StreamOf(session), session.MapId);
        public Task RestoreAnomalyAsync(GameSession session) => AnomalyHandler.OnMapLoadedAsync(StreamOf(session), session.MapId);
        public Task RestoreGargandiasAsync(GameSession session) => GargandiasHandler.OnMapLoadedAsync(StreamOf(session), session.MapId);
        public Task RestoreGuildHallAsync(GameSession session)
            => GuildHallHandler.OnMapLoadedAsync(StreamOf(session), session.CharacterId, session.MapId);
        public int SubAreaOf(long mapId) => MapManager.GetMapInfo(mapId)?.SubAreaId ?? 0;
        public Task EnterQuestMapAsync(GameSession session, int subAreaId)
            => Managers.Quests.OnMapEnteredAsync(StreamOf(session), session.MapId, subAreaId);
        public Task EnterAchievementMapAsync(GameSession session, int subAreaId)
            => Managers.Achievements.OnMapEnteredAsync(StreamOf(session), session.MapId, subAreaId);
        public Task EnterConquestMapAsync(GameSession session, int subAreaId)
            => ConquestHandler.OnMapEnteredAsync(StreamOf(session), session.CharacterId, subAreaId);
        public Task EnterLingeringRoomAsync(GameSession session)
            => Managers.LingeringRooms.OnMapEnteredAsync(StreamOf(session), session.CharacterId, session.MapId);
        public Task RestorePhantomAsync(GameSession session) => PhantomHandler.OnMapLoadedAsync(StreamOf(session));
        public Task ShowMapStagingAsync(GameSession session)
            => Managers.MapStagings.ShowAsync(StreamOf(session), Managers.MapStagings.OnArrival(session.MapId));
        public Task PlacePendingBonusItemsAsync(GameSession session) => BonusItemHandler.PlacePendingAsync(StreamOf(session));

        private static NetworkStream StreamOf(GameSession session)
            => session.Stream ?? throw new InvalidOperationException("Map information requires a connected game session.");
    }
}
