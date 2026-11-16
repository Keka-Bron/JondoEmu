using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal interface IQuestStateBackend
{
    QuestsEvent? Journal(GameSession session);
    IReadOnlyList<int> Followed(long characterId);
    void Unfollow(long characterId, int questId);
    NpcsMapQuestStatusUpdateEvent? Marks(GameSession session, long mapId);
}

/// <summary>Owner-codec quest delivery, retaining journal-before-followed and cleanup timing.</summary>
internal sealed class QuestStateService(IQuestStateBackend backend)
{
    internal static QuestStateService Live { get; } = new(new LiveBackend());

    internal async Task SendJournalAsync(GameSession session, bool box = false)
    {
        long characterId = session.CharacterId;
        var journal = backend.Journal(session);
        if (journal == null) return;
        await session.SendAsync(journal);
        Console.WriteLine($"[Quests] Journal sent: {journal.ActiveQuests.Count} in progress, {journal.FinishedQuests.Count} done.");
        if (!box) return;

        // Read and clean the persisted tracker only after the journal has sent.
        // Reuse the same detached active entries, without protobuf construction or parsing.
        var followed = new List<QuestActive>();
        foreach (int questId in backend.Followed(characterId).ToArray())
        {
            var entry = journal.ActiveQuests.FirstOrDefault(quest => quest.QuestId == questId);
            if (entry != null) followed.Add(entry);
            else backend.Unfollow(characterId, questId);
        }
        await session.SendAsync(new QuestsFollowedEvent(followed.ToArray()));
    }

    internal async Task SendMarksAsync(GameSession session, long mapId)
    {
        var marks = backend.Marks(session, mapId);
        if (marks == null) return;
        await session.SendAsync(marks);
        var npcs = marks.MapInformation.SelectMany(map => map.NpcsWithQuests).ToArray();
        Console.WriteLine($"[Quests] Marks of map {mapId}: {npcs.Count(npc => npc.QuestsToStart.Count > 0)} of {npcs.Length} NPCs " +
            $"with something to offer, {npcs.Sum(npc => npc.QuestsToStart.Count)} quest(s); {npcs.Sum(npc => npc.QuestsToValidate.Count)} under way waiting on them.");
    }

    private sealed class LiveBackend : IQuestStateBackend
    {
        public QuestsEvent? Journal(GameSession session) => Quests.JournalSnapshot(session);
        public IReadOnlyList<int> Followed(long characterId) => FollowedQuests.Of(characterId);
        public void Unfollow(long characterId, int questId) => FollowedQuests.Unfollow(characterId, questId);
        public NpcsMapQuestStatusUpdateEvent? Marks(GameSession session, long mapId) => Quests.MarksSnapshot(session, mapId);
    }
}
