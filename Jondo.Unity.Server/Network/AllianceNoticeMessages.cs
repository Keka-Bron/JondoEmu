using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

/// <summary>
/// The alliance MOTD and bulletin share content, seconds, author and member ID. Both
/// historical «E X A M P L E» joining notices contained only the alliance's founding date.
/// </summary>
internal static class AllianceNoticeMessages
{
    internal static GameEvent Snapshot(int kind, AllianceStore.Notice notice)
    {
        // Both historical empty notices still carried a present information block.
        var information = new SocialNoticeInformation(notice.Text ?? "", notice.WhenS,
            notice.AuthorName ?? "", notice.AuthorId);
        return kind switch
        {
            AllianceStore.NoticeMessage => new AllianceMotdEvent(information),
            AllianceStore.NoticeBulletin => new AllianceBulletinEvent(information),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }
}
