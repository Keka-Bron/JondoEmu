using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal interface IGuildPresenceBackend
{
    IReadOnlyList<GuildStore.Member> Members(long guildId);
    GameSession? FindSession(long characterId);
}

internal sealed class GuildPresenceService(IGuildPresenceBackend backend)
{
    internal static GuildPresenceService Live { get; } = new(new LiveBackend());

    internal async Task BroadcastAsync(long guildId, long characterId, bool online, byte[] memberUpdate)
    {
        var members = backend.Members(guildId).ToArray();
        var warning = new GuildMemberOnlineStatusEvent(characterId, true);
        foreach (var member in members)
        {
            if (member.CharacterId == characterId) continue;
            var session = backend.FindSession(member.CharacterId);
            if (session == null) continue;
            await session.SendAsync(memberUpdate);
            // This event is the opt-in login warning, not a second offline roster update.
            if (online && session.State.WarnGuildConnections) await session.SendAsync(warning);
        }
    }

    private sealed class LiveBackend : IGuildPresenceBackend
    {
        public IReadOnlyList<GuildStore.Member> Members(long guildId) => GuildStore.Members(guildId);
        public GameSession? FindSession(long characterId) => SessionRegistry.FindByCharacter(characterId);
    }
}
