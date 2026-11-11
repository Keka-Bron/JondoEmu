using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal interface IArenaPlayerInformationBackend
{
    DateTime UtcNow { get; }
    IReadOnlyList<KoliseoLadder.Standing> Standings(long characterId, int level);
    DateTime? BannedUntil(long characterId, DateTime nowUtc);
    void LeaveQueue(long characterId);
    int LastMode(long characterId);
}

/// <summary>Shares entry/return construction and owns the arena-return send ordering.</summary>
internal sealed class ArenaPlayerInformationService(IArenaPlayerInformationBackend backend)
{
    internal static ArenaPlayerInformationService Live { get; } = new(new LiveBackend());

    internal ArenaUpdatePlayerInformationEvent Snapshot(long characterId, int level)
    {
        var now = backend.UtcNow;
        return ArenaPlayerInformationMessages.Snapshot(backend.Standings(characterId, level),
            backend.BannedUntil(characterId, now), now);
    }

    internal async Task ReturnAsync(GameSession session, int? uid = null)
    {
        long characterId = session.CharacterId;
        backend.LeaveQueue(characterId);
        await session.SendAsync(Snapshot(characterId, session.State.CharacterLevel));
        // Keep the existing response and queue notification until their separate migration.
        if (uid is { } requestId)
            await session.SendAsync(ConnectionProtocol.Answer(Op.Lsr, null, requestId));
        await session.SendAsync(ConnectionProtocol.Push(Op.Lsx,
            KoliseoHandler.BuildLeftQueue(backend.LastMode(characterId))));
    }

    private sealed class LiveBackend : IArenaPlayerInformationBackend
    {
        public DateTime UtcNow => DateTime.UtcNow;
        public IReadOnlyList<KoliseoLadder.Standing> Standings(long characterId, int level)
            => KoliseoLadder.AllOf(characterId, level);
        public DateTime? BannedUntil(long characterId, DateTime nowUtc)
            => KoliseoOffers.BannedUntil(characterId, nowUtc);
        public void LeaveQueue(long characterId) => KoliseoQueue.Leave(characterId);
        public int LastMode(long characterId) => KoliseoOffers.LastMode(characterId);
    }
}
