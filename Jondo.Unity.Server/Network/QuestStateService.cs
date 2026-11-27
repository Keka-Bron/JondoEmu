using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal interface IQuestStateBackend
{
    QuestsEvent? Journal(GameSession session);
    IReadOnlyList<int> Followed(long characterId);
    void Follow(long characterId, int questId);
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

    /// <summary>
    /// A companion taken in hand mid-session: his journal, and the followed box made his.
    /// </summary>
    /// <remarks>
    /// The client keeps its box across the switch and adds what an iel brings to it: the new one's
    /// whole box, as on entering the world, showed every quest both follow -- with quests shared,
    /// all of them -- once more at each Tab. So the box shown is the one played before's: what of
    /// it the new one does not have under way is taken out by its ied, before his journal replaces
    /// the window's; what he has stays, followed by him too from now on as the box shows it, and
    /// its step told as his (idu); only what he follows besides goes in the iel.
    /// </remarks>
    internal async Task SendSwitchedJournalAsync(GameSession session, long before)
    {
        long characterId = session.CharacterId;
        var journal = backend.Journal(session);
        if (journal == null) return;
        var shown = backend.Followed(before).ToArray();
        foreach (int questId in shown)
        {
            if (journal.ActiveQuests.All(quest => quest.QuestId != questId))
                await session.SendAsync(new QuestValidatedEvent(questId));
        }
        await session.SendAsync(journal);

        var mine = backend.Followed(characterId).ToArray();
        foreach (int questId in shown)
        {
            var entry = journal.ActiveQuests.FirstOrDefault(quest => quest.QuestId == questId);
            if (entry == null) continue;
            if (!mine.Contains(questId)) backend.Follow(characterId, questId);
            await session.SendAsync(new QuestStepInformationEvent(entry));
        }
        var added = new List<QuestActive>();
        foreach (int questId in mine)
        {
            if (shown.Contains(questId)) continue;
            var entry = journal.ActiveQuests.FirstOrDefault(quest => quest.QuestId == questId);
            if (entry != null) added.Add(entry);
            else backend.Unfollow(characterId, questId);
        }
        await session.SendAsync(new QuestsFollowedEvent(added.ToArray()));
        Console.WriteLine($"[Quests] Box of {before} made {characterId}'s: {shown.Length} shown, {added.Count} added.");
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
        public void Follow(long characterId, int questId) => FollowedQuests.Follow(characterId, questId);
        public void Unfollow(long characterId, int questId) => FollowedQuests.Unfollow(characterId, questId);
        public NpcsMapQuestStatusUpdateEvent? Marks(GameSession session, long mapId) => Quests.MarksSnapshot(session, mapId);
    }
}
