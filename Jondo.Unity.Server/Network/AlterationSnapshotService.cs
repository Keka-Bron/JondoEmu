using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal interface IAlterationSnapshotBackend
{
    DateTime UtcNow { get; }
    IReadOnlyList<Alterations.Active> Of(long characterId);
    IReadOnlyList<Alterations.Active> Expire(long characterId, DateTime nowUtc);
    (IReadOnlyList<Alterations.Active> Listed, IReadOnlyList<Alterations.Active> Spent) CountFight(long characterId, bool won);
    Task RefreshSlotsAsync(GameSession session, bool won);
    Task TellLostAsync(GameSession session, Alterations.Active active, DateTime nowUtc);
}

/// <summary>Owns typed snapshot delivery and post-fight snapshot/slots/loss ordering.</summary>
internal sealed class AlterationSnapshotService(AlterationMessages messages, IAlterationSnapshotBackend backend)
{
    internal Task ListAsync(GameSession session)
    {
        var now = backend.UtcNow;
        return session.SendAsync(messages.Snapshot(backend.Of(session.CharacterId), now));
    }

    internal async Task AfterFightAsync(GameSession session, bool won)
    {
        using var scope = SessionContext.Push(session);
        var now = backend.UtcNow;
        var expired = backend.Expire(session.CharacterId, now);
        var (listed, spent) = backend.CountFight(session.CharacterId, won);
        // The exhausted entries still belong to the snapshot. Their losses follow
        // slot updates, and the caller's characteristics refresh comes after all of it.
        await session.SendAsync(messages.Snapshot(listed, now));
        await backend.RefreshSlotsAsync(session, won);
        foreach (var active in expired.Concat(spent)) await backend.TellLostAsync(session, active, now);
    }
}
