using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

/// <summary>
/// Whole alliance rank lists, including the custom names, rights and icons measured
/// in the «E X A M P L E» capture. Stored order and meaningful empty blocks survive.
/// </summary>
internal static class AllianceRankMessages
{
    internal static AllianceRanksEvent Snapshot(IEnumerable<AllianceStore.Rank> ranks)
        => new(ranks.Select(rank => new RankInformation(rank.Name,
            new RankInformationPrivateInformation(rank.Flag, rank.Rights.ToArray()),
            new RankInformationOrderAndGfx(rank.Icon, rank.Order), rank.Id)).ToArray());
}
