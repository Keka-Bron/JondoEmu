using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal interface IAllianceNoticeBackend
{
    AllianceStore.Notice Notice(AllianceStore.Alliance alliance, int kind);
    IReadOnlyList<AllianceStore.Member> Members(long allianceId);
    GameSession? FindSession(long characterId);
}

internal sealed class AllianceNoticeService(IAllianceNoticeBackend backend)
{
    internal static AllianceNoticeService Live { get; } = new(new LiveBackend());

    internal async Task SendAsync(GameSession session, AllianceStore.Alliance alliance)
    {
        // Keep source reads interleaved with delivery: a bulletin read failure happens
        // after the MOTD has been delivered, just as in the original continuation.
        await session.SendAsync(AllianceNoticeMessages.Snapshot(AllianceStore.NoticeMessage,
            backend.Notice(alliance, AllianceStore.NoticeMessage)));
        await session.SendAsync(AllianceNoticeMessages.Snapshot(AllianceStore.NoticeBulletin,
            backend.Notice(alliance, AllianceStore.NoticeBulletin)));
    }

    internal async Task<int> BroadcastAsync(long allianceId, int kind, AllianceStore.Notice savedNotice)
    {
        var snapshot = AllianceNoticeMessages.Snapshot(kind, savedNotice);
        var members = backend.Members(allianceId).ToArray();
        int told = 0;
        foreach (var member in members)
        {
            var session = backend.FindSession(member.CharacterId);
            if (session == null) continue;
            await session.SendAsync(snapshot);
            told++;
        }
        return told;
    }

    private sealed class LiveBackend : IAllianceNoticeBackend
    {
        public AllianceStore.Notice Notice(AllianceStore.Alliance alliance, int kind) => AllianceStore.NoticeOf(alliance, kind);
        public IReadOnlyList<AllianceStore.Member> Members(long allianceId) => AllianceStore.Members(allianceId);
        public GameSession? FindSession(long characterId) => SessionRegistry.FindByCharacter(characterId);
    }
}
