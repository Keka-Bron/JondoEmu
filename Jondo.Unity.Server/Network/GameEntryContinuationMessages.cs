using System.Globalization;
using Jondo.Unity.Protocol.Game;

namespace Jondo.Unity.Server.Network;

/// <summary>Session identity and account gifts, after initial state and its live hooks.</summary>
internal sealed class GameEntryContinuationMessages(GiftService gifts)
{
    internal static GameEntryContinuationMessages Live { get; } = new(GiftService.Live);

    internal async Task<int> SendAsync(GameSession session)
    {
        await session.SendAsync(new HaapiSessionEvent(session.HaapiSessionUid.ToString(CultureInfo.InvariantCulture)));
        await gifts.SendAsync(session);
        return 2;
    }
}
