using Jondo.Unity.Protocol.Game;
using Jondo.Unity.World.Emotes;

namespace Jondo.Unity.Server.Network;

internal interface IEmoteOwnershipBackend
{
    bool Exists(int emoteId);
    void Save(long characterId, int emoteId);
}

/// <summary>Character-owned emote snapshots and write-through learning, under the caller's session turn.</summary>
internal sealed class EmoteOwnershipService(IEmoteOwnershipBackend backend)
{
    internal static HashSet<int> Owned(IEnumerable<int> learned, IEnumerable<int> administratorGrants)
    {
        var owned = new HashSet<int>(EmoteRules.Starting);
        owned.UnionWith(learned);
        owned.UnionWith(administratorGrants);
        return owned;
    }

    internal static EmotesEvent Snapshot(IEnumerable<int> owned) => new(owned.Distinct().Order().ToArray());

    internal static Task SendSnapshotAsync(GameSession session)
        => session.SendAsync(Snapshot(session.State.Emotes));

    internal async Task<bool> LearnAsync(GameSession session, int emoteId)
    {
        var state = session.State;
        if (!backend.Exists(emoteId) || state.Emotes.Contains(emoteId)) return false;

        // A failed write must neither grant ownership nor announce success. Once
        // saved, ownership survives a failed send; a retry cannot learn it twice.
        backend.Save(session.CharacterId, emoteId);
        state.Emotes.Add(emoteId);
        await session.SendAsync(new EmoteAddedEvent(emoteId));
        Console.WriteLine($"[Attitudes] {state.CharacterName} learns attitude {emoteId}.");
        return true;
    }
}
