using Jondo.Unity.Protocol.Game;

namespace Jondo.Unity.Server.Network;

/// <summary>Detached quest journal entries and the replacement map-mark index.</summary>
internal static class QuestMessages
{
    internal static QuestActive Active(int questId, int stepId, IEnumerable<int> objectives, IReadOnlyCollection<int> done)
        => new(new(objectives.Select(id => new QuestObjective([], id, null, !done.Contains(id))).ToArray(), stepId), questId);

    internal static QuestsEvent Journal(IEnumerable<QuestActive> active, IEnumerable<int> finished)
        // Finished tag 1 is indeterminate and omitted, not an invented count. The
        // reinitialized-done list stays empty under the existing server policy.
        // See docs/populated-unmapped-fields.md#quest-journal-construction-policy.
        => new(active.ToArray(), finished.Select(id => new QuestFinished(id)).ToArray(), []);

    internal static NpcsMapQuestStatusUpdateEvent Marks(long mapId,
        IEnumerable<(long Actor, IReadOnlyList<int> Offers, IReadOnlyList<int> InProgress)> actors)
    {
        var marked = actors.Where(row => row.Offers.Count > 0 || row.InProgress.Count > 0)
            .Select(row => new NpcWithQuest(row.Offers.ToArray(), row.InProgress.ToArray(), checked((int)row.Actor))).ToArray();
        // No map block when no NPC has anything: this is an explicit empty
        // replacement, rather than a map-id-only block or silence.
        return new(marked.Length == 0 ? [] : [new MapNpcQuest(marked, mapId)]);
    }
}
