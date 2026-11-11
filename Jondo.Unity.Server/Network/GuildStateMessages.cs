using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

/// <summary>Direct guild identity, silent membership and general-information snapshots.</summary>
internal static class GuildStateMessages
{
    internal static GuildInformation Information(GuildStore.Guild guild)
        => new(checked((int)guild.Id), guild.Name, guild.Level,
            new GuildSocialInformation(new SocialEmblem(
                guild.EmblemSymbol,
                // Historical storage name: this is the background shape, not a color.
                guild.EmblemSymbolColor, guild.EmblemBackground, guild.EmblemSymbolRgb)));

    /// <summary>
    /// Entry/identity updates use silent membership, not the joined popup. The old
    /// contribution captures carried 10 then 20 here; retain that persisted total.
    /// The separately reviewed context value stays zero in these sends (the old
    /// builder omitted it); map-actor membership has its own construction policy.
    /// </summary>
    internal static GuildMembershipEvent Membership(GuildStore.Guild guild, int rankId, long contributed)
        => new(Information(guild), rankId, checked((int)contributed), UnknownUsedContextValue: 0);

    /// <summary>
    /// Captured level 1 headers used next floor 50; level 4 used 150/181/210.
    /// The next floor was once mistaken for a member maximum. Hall ownership is
    /// now supplied explicitly, including absence; the old builder omitted it.
    /// No general guild score is persisted, so score retains its zero policy.
    /// </summary>
    internal static GuildGeneralInformationEvent Header(GuildStore.Guild guild, int memberCount, int? hallId)
        => new(guild.FoundedUtc, hallId, guild.Level, Score: 0,
            GuildLevels.FloorOf(guild.Level), guild.Experience, guild.GuildKamas,
            GuildLevels.NextFloorOf(guild.Level), memberCount);
}
