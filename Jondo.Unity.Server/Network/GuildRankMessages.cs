using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

/// <summary>
/// A whole rank-list refresh, in stored display order. The founding capture used
/// translation keys guild.rank.N.name; the rank-management capture renamed 1/2,
/// changed 2's rights and inserted 5 before 4. Both travel as full snapshots.
/// </summary>
internal static class GuildRankMessages
{
    internal static GuildRanksEvent Snapshot(IEnumerable<GuildStore.Rank> ranks)
        => new(ranks.Select(rank => new RankInformation(rank.Name,
            new RankInformationPrivateInformation(rank.Flag, GuildRankRights.Decode(rank.Rights)),
            new RankInformationOrderAndGfx(rank.Icon, rank.Order), rank.Id)).ToArray());
}
