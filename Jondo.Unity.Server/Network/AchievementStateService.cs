using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal interface IAchievementStateBackend
{
    AchievementsEvent? Snapshot(GameSession source);
    IReadOnlyList<int> CheckEverything(GameSession source);
    void CreditAndSave(GameSession source, int achievementId, int level);
    Task NotifyMapAsync(GameSession source, AchievementFinishedInformationEvent message);
}

/// <summary>Achievement delivery with separate credited owner and transport recipient.</summary>
internal sealed class AchievementStateService(IAchievementStateBackend backend)
{
    internal static AchievementStateService Live { get; } = new(new LiveBackend());

    internal async Task SendListAsync(GameSession source)
    {
        using var context = SessionContext.Push(source);
        var snapshot = backend.Snapshot(source);
        if (snapshot == null) return;
        await source.SendAsync(snapshot);

        var earned = backend.CheckEverything(source);
        source.State.AchievementLevelChecked = source.State.CharacterLevel;
        await AnnounceAsync(source, source, earned);
    }

    internal async Task AnnounceAsync(GameSession source, GameSession recipient, IReadOnlyList<int> earned)
    {
        using var context = SessionContext.Push(source);
        foreach (int achievementId in earned)
        {
            var state = source.State;
            var achievement = AchievementMessages.Earned(source.CharacterId, state.CharacterLevel, achievementId);
            var information = new AchievementFinishedInformationEvent(source.CharacterId, state.CharacterName, achievement);
            backend.CreditAndSave(source, achievementId, achievement.FinishedLevel!.Value);
            await recipient.SendAsync(new AchievementFinishedEvent(achievement));

            // Preserve the existing inferred audience: only a non-fighting source broadcasts,
            // but recipients are not filtered by fight ID. Companions still exclude themselves.
            if (!state.IsInFight) await backend.NotifyMapAsync(source, information);
            Console.WriteLine($"[Achievements] {state.CharacterName} earns {achievementId}.");
        }
    }

    private sealed class LiveBackend : IAchievementStateBackend
    {
        public AchievementsEvent? Snapshot(GameSession source)
            => source.State.Achievements is { } log
                ? AchievementMessages.Snapshot(Achievements.ListOf(log, source.CharacterId, source.State.CharacterLevel)) : null;

        public IReadOnlyList<int> CheckEverything(GameSession source)
            => source.State.Achievements!.CheckEverything();

        public void CreditAndSave(GameSession source, int achievementId, int level)
        {
            source.State.Achievements?.Credit(achievementId, source.CharacterId);
            source.State.Achievements?.NoteLevel(achievementId, level);
            DatabaseManager.SaveAchievement(source.CharacterId, achievementId, claimed: false, level: level);
        }

        public Task NotifyMapAsync(GameSession source, AchievementFinishedInformationEvent message)
            => SessionRegistry.BroadcastToMapAsync(source.MapId, message, source.Id);
    }
}
