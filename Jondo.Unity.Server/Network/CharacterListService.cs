using Jondo.Unity.Protocol.Game;

namespace Jondo.Unity.Server.Network;

/// <summary>Shared login and creation/deletion refresh, under the caller's session turn.</summary>
internal sealed class CharacterListService(CharacterListMessages messages, GiftService gifts)
{
    internal static CharacterListService Live { get; } = new(CharacterListMessages.Live, GiftService.Live);

    internal async Task SendAsync(GameSession session, IReadOnlyList<DbCharacter> characters,
        bool withInitialization = true)
    {
        using var context = SessionContext.Push(session);
        if (withInitialization)
        {
            await session.SendAsync(new QueueStatusEvent(1, 1));
            await session.SendAsync(new QueueStatusEvent(1, 0));
            await session.SendAsync(new QueueStatusEvent(0, 0));
        }
        await session.SendAsync(messages.Snapshot(characters));
        // A resumable fight gets only its list here; the entry service sends force-selection next.
        if (withInitialization) await gifts.SendAsync(session);
    }
}
