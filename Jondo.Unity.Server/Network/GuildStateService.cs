using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal interface IGuildStateBackend
{
    int? HallId(long guildId);
    long ContributedBy(long characterId);
    IReadOnlyList<GuildStore.Member> Members(long guildId);
    GameSession? FindSession(long characterId);
}

/// <summary>Shares guild state sources and sends through each owning recipient's codec.</summary>
internal sealed class GuildStateService(IGuildStateBackend backend)
{
    internal static GuildStateService Live { get; } = new(new LiveBackend());

    internal GuildMembershipEvent Membership(GuildStore.Guild guild, int rankId, long characterId)
        => GuildStateMessages.Membership(guild, rankId, backend.ContributedBy(characterId));

    internal GuildGeneralInformationEvent Header(GuildStore.Guild guild, int memberCount)
        => GuildStateMessages.Header(guild, memberCount, backend.HallId(guild.Id));

    internal Task SendHeaderAsync(GameSession session, GuildStore.Guild guild)
        => session.SendAsync(Header(guild, backend.Members(guild.Id).Count));

    internal async Task TellHeaderAsync(GuildStore.Guild guild)
    {
        var members = backend.Members(guild.Id).ToArray();
        // Capture once before any await; all recipients see the same guild state,
        // while their own codecs perform encoding. Reuse the caller's existing turn.
        var header = Header(guild, members.Length);
        foreach (var member in members)
        {
            var session = backend.FindSession(member.CharacterId);
            if (session != null) await session.SendAsync(header);
        }
    }

    private sealed class LiveBackend : IGuildStateBackend
    {
        public int? HallId(long guildId) => GuildHalls.ChoiceOf(guildId)?.HallId;
        public long ContributedBy(long characterId) => GuildStore.ContributedBy(characterId);
        public IReadOnlyList<GuildStore.Member> Members(long guildId) => GuildStore.Members(guildId);
        public GameSession? FindSession(long characterId) => SessionRegistry.FindByCharacter(characterId);
    }
}
