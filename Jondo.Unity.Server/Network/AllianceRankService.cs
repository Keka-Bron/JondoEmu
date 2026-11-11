using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal interface IAllianceRankBackend
{
    IReadOnlyList<AllianceStore.Rank> Ranks(long allianceId);
    IReadOnlyList<AllianceStore.Member> Members(long allianceId);
    GameSession? FindSession(long characterId);
}

/// <summary>One rank source/construction policy for entry, joining and live refreshes.</summary>
internal sealed class AllianceRankService(IAllianceRankBackend backend)
{
    internal static AllianceRankService Live { get; } = new(new LiveBackend());

    internal AllianceRanksEvent Snapshot(long allianceId)
        => AllianceRankMessages.Snapshot(backend.Ranks(allianceId));

    internal Task SendAsync(GameSession session, long allianceId)
        => session.SendAsync(Snapshot(allianceId));

    internal async Task<int> BroadcastAsync(long allianceId)
    {
        var snapshot = Snapshot(allianceId);
        var members = backend.Members(allianceId).ToArray();
        int told = 0;
        // Keep the existing sequential failure behavior and caller-owned turn.
        // Capture once, but let each recipient's codec encode its own event.
        foreach (var member in members)
        {
            var session = backend.FindSession(member.CharacterId);
            if (session == null) continue;
            await session.SendAsync(snapshot);
            told++;
        }
        return told;
    }

    private sealed class LiveBackend : IAllianceRankBackend
    {
        public IReadOnlyList<AllianceStore.Rank> Ranks(long allianceId) => AllianceStore.Ranks(allianceId);
        public IReadOnlyList<AllianceStore.Member> Members(long allianceId) => AllianceStore.Members(allianceId);
        public GameSession? FindSession(long characterId) => SessionRegistry.FindByCharacter(characterId);
    }
}
