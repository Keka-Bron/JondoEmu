using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

/// <summary>
/// An explicitly captured map instance, not an ambient SessionContext lookup.
/// Recipients are snapshotted at send time; events include the sender unless excluded.
/// </summary>
public sealed class GameMapAudience
{
    private readonly MapAudienceScope _scope;
    private readonly IGameCodec _codec;

    internal GameMapAudience(MapAudienceScope scope, IGameCodec codec)
    {
        _scope = scope;
        _codec = codec;
    }

    public long MapId => _scope.MapId;

    public Task<int> SendAsync(GameEvent message, Guid? exceptSessionId = null, long? fightId = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        var targets = SessionRegistry.All()
            .Where(s => _scope.Contains(s) && s.Id != exceptSessionId && SessionRegistry.Hears(s.State.FightId, fightId))
            .ToArray();
        // All current sessions use the pinned Game profile. Encode once, then use
        // each recipient's existing transport; never acquire their gameplay locks.
        byte[] packet = _codec.Encode(new GameFrame(message));
        return SessionRegistry.SendToAsync(targets, packet);
    }
}

internal readonly record struct MapAudienceScope(
    long MapId, long? BagOwner, (long MapId, int ElementId, int Instance)? House)
{
    internal static MapAudienceScope Capture(SessionState state) => new(
        state.MapId,
        Merkasako.IsHavenBag(state.MapId) ? MerkasakoHandler.BagOwnerOf(state) : null,
        Houses.IsInterior(state.MapId) ? HouseHandler.HouseOf(state) : null);

    internal bool Contains(GameSession session) => session.IsInWorld && session.MapId == MapId &&
        (!BagOwner.HasValue || MerkasakoHandler.BagOwnerOf(session.State) == BagOwner.Value) &&
        (!House.HasValue || HouseHandler.HouseOf(session.State) == House.Value);
}
