using Jondo.Unity.Server.Mods;
using Jondo.Unity.Server.Network;
using Jondo.Unity.ServerMods;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>Host integration for characters attached to an existing session.</summary>
    internal static class CompanionSessions
    {
        internal static ICompanionSessionPolicy? Policy => ServerModHooks.Get<ICompanionSessionPolicy>();

        internal static IReadOnlyList<ICompanion> Of(long controllerId) => Policy?.Of(controllerId) ?? [];
        internal static GameSession? SessionOf(long characterId) => Policy?.Find(characterId)?.Session as GameSession;
        internal static void OnSessionLeaving(GameSession session) => Policy?.MasterLeft(session);
        internal static bool SelectCharacter(long characterId, Func<bool> load)
            => Policy?.SelectCharacter(characterId, load) ?? load();
        internal static bool SwitchPending(long accountId) => Policy?.SwitchPending(accountId) == true;
        internal static long TakeSwitch(long accountId) => Policy?.TakeSwitch(accountId) ?? 0;
        internal static void Regroup(GameSession session) => Policy?.Regroup(session);
    }

    internal static class QuestSharing
    {
        internal static IQuestSharingPolicy? Policy => ServerModHooks.Get<IQuestSharingPolicy>();

        internal static LagCause? CouldNotStart(GameSession session, int questId)
            => session.State.Quests is { } log && !log.Active(questId) && !log.Finished(questId) && !log.CanStart(questId, out _)
                ? new LagCause(QuestLagReason.Requirements)
                : null;

        internal static Task ShareQuestActionAsync(Func<GameSession, Task> apply,
            Func<ICompanion, bool>? participates = null, IReadOnlyCollection<int>? quests = null,
            Func<GameSession, LagCause?>? explain = null)
            => Policy?.ShareQuestActionAsync(SessionContext.Current,
                async session =>
                {
                    using var context = SessionContext.Push((GameSession)session);
                    await apply((GameSession)session);
                },
                participates, quests,
                explain == null ? null : session =>
                {
                    using var context = SessionContext.Push((GameSession)session);
                    return explain((GameSession)session);
                }) ?? Task.CompletedTask;

        internal static Task<bool> PoolQuestItemsAsync(GameSession session, IEnumerable<(int Item, int Count)> asked,
            Func<GameSession, bool> follows)
            => Policy?.PoolQuestItemsAsync(session, asked, companion =>
            {
                using var context = SessionContext.Push((GameSession)companion);
                return follows((GameSession)companion);
            }) ?? Task.FromResult(true);
    }
}
