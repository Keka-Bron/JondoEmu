using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal interface IGuildRankBackend
{
    long? GuildOf(long characterId);
    IReadOnlyList<GuildStore.Rank> Ranks(long guildId);
}

/// <summary>One source and construction policy for entry, joining and rank refreshes.</summary>
internal sealed class GuildRankService(IGuildRankBackend backend)
{
    internal static GuildRankService Live { get; } = new(new LiveBackend());

    internal GuildRanksEvent Initial(long characterId)
        => backend.GuildOf(characterId) is { } guildId
            ? Snapshot(guildId)
            : GuildRankMessages.Snapshot(GuildStore.DefaultRanks(0));

    internal GuildRanksEvent Snapshot(long guildId)
        => GuildRankMessages.Snapshot(backend.Ranks(guildId));

    internal Task SendAsync(GameSession session, long guildId)
        => session.SendAsync(Snapshot(guildId));

    private sealed class LiveBackend : IGuildRankBackend
    {
        public long? GuildOf(long characterId) => GuildStore.GuildOf(characterId)?.Id;
        public IReadOnlyList<GuildStore.Rank> Ranks(long guildId) => GuildStore.Ranks(guildId);
    }
}
