using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal static class AchievementMessages
{
    internal static AchievementsEvent Snapshot(IEnumerable<(int Achievement, long AchievedBy, int UnclaimedLevel)> rows)
        => new(rows
            .Select(row => new AchievedAchievement(row.UnclaimedLevel == 0 ? null : row.UnclaimedLevel,
                row.AchievedBy, row.Achievement, 0)).ToArray());

    internal static AchievedAchievement Earned(long characterId, int level, int achievementId)
        => new(Achievements.LevelOnTheWire(level), characterId, achievementId, 0);
}
