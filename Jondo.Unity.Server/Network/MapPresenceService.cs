using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal interface IMapPresenceBackend
{
    DbCharacter? FindCharacter(long characterId);
    GameRolePlayShowActorsEvent Refreshed(GameSession session, DbCharacter character);
    Parties.Party? PartyOf(long characterId);
    Task LeaderMovedAsync(GameSession session);
}

/// <summary>Typed roleplay presence, with explicitly captured source/destination instances.</summary>
internal sealed class MapPresenceService(IMapPresenceBackend backend)
{
    internal static MapPresenceService Live { get; } = new(new LiveBackend());

    internal async Task<int> AnnounceAsync(GameSession session, DbCharacter character)
    {
        using var context = SessionContext.Push(session);
        return await session.Map.SendAsync(backend.Refreshed(session, character), exceptSessionId: session.Id);
    }

    internal async Task RefreshAsync(GameSession session, DbCharacter character, bool includeMap = true)
    {
        using var context = SessionContext.Push(session);
        var message = backend.Refreshed(session, character);
        var audience = session.Map;
        await session.SendAsync(message);
        if (includeMap) await SessionRegistry.SendToAsync(audience.Recipients(session.Id), message);
    }

    internal async Task<(int seVa, int llega)> TravelAsync(GameSession session, GameMapAudience previousMap, int? direction = null)
    {
        if (!session.IsInWorld || session.CharacterId <= 0) return (0, 0);
        int left = 0;
        if (previousMap.MapId > 0 && !previousMap.SameInstance(session.Map))
        {
            left = await RemoveAsync(previousMap.Recipients(session.Id), session.CharacterId, direction);
            if (previousMap.MapId != session.MapId) await backend.LeaderMovedAsync(session);
        }
        var character = backend.FindCharacter(session.CharacterId);
        int arrived = character == null ? 0 : await AnnounceAsync(session, character);
        if (left > 0 || arrived > 0)
            Program.LogDebug($"[Move] {character?.Name}: {previousMap.MapId} -> {session.MapId}. Told {left} on the map left and {arrived} on the one found.");
        return (left, arrived);
    }

    internal Task<int> RemoveAsync(GameMapAudience audience, long characterId, Guid exceptSessionId, int? direction = null)
        => RemoveAsync(audience.Recipients(exceptSessionId), characterId, direction);

    internal async Task<int> RemoveAsync(IReadOnlyList<GameSession> targets, long characterId, int? direction = null)
    {
        var party = backend.PartyOf(characterId);
        var results = await Task.WhenAll(targets.Select(async target =>
        {
            try
            {
                if (direction.HasValue && party != null && backend.PartyOf(target.CharacterId) == party)
                    await target.SendAsync(new MapChangeOrientationEvent(characterId, MapActorMessages.Facing(direction.Value)));
                await target.SendAsync(new ContextRemoveElementEvent(characterId));
                return true;
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[Sessions] Send to {target.Id} failed: {ex.Message}");
                return false;
            }
        }));
        return results.Count(delivered => delivered);
    }

    private sealed class LiveBackend : IMapPresenceBackend
    {
        public DbCharacter? FindCharacter(long characterId) => DatabaseManager.GetCharacterById(characterId);
        public GameRolePlayShowActorsEvent Refreshed(GameSession session, DbCharacter character)
            => MapActorMessages.Refreshed(character, session.State.CellId, session.State.Orientation, session.AccountId);
        public Parties.Party? PartyOf(long characterId) => Parties.Of(characterId);
        public Task LeaderMovedAsync(GameSession session) => PartyFollowHandler.LeaderMovedAsync(session);
    }
}
