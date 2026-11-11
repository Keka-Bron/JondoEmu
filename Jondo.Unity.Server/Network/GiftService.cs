using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal sealed class GiftService(Action<long> grantDue, Func<long, IReadOnlyList<Gifts.Gift>> gifts,
    Func<IEnumerable<GameSession>> online, Action<string> log)
{
    internal static GiftService Live { get; } = new(account => VeteranRewards.GrantDue(account),
        Gifts.Of, SessionRegistry.InWorld, Console.WriteLine);

    internal GameActionItemListEvent Snapshot(long accountId)
    {
        grantDue(accountId);
        return GiftMessages.Snapshot(gifts(accountId));
    }

    internal Task SendAsync(GameSession session) => session.SendAsync(Snapshot(session.AccountId));

    internal async Task BroadcastAsync(long accountId)
    {
        var snapshot = Snapshot(accountId);
        foreach (var session in online().Where(session => session.AccountId == accountId))
        {
            // Live refresh has always isolated a disconnected recipient and continued.
            try { await session.SendAsync(snapshot); }
            catch (Exception ex) { log($"[Gifts] Could not tell account {accountId}: {ex.Message}"); }
        }
    }
}
