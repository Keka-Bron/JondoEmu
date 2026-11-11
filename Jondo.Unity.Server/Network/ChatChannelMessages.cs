using Jondo.Unity.Protocol.Game;

namespace Jondo.Unity.Server.Network;

/// <summary>Session-owned chat display/subscription policy, independent of entry reference rows.</summary>
internal static class ChatChannelMessages
{
    // Server defaults: conversational, private, system and combat channels start on.
    // Public advertising/help/community feeds start off. These are policy choices,
    // not the captured account's choices (which, for example, disabled fight output).
    // Reviewed numeric slots with unknown meanings remain supported opaque values;
    // they start disabled but a client's explicit subscription can retain them.
    private static readonly ChatChannel[] Channels = Enum.GetValues<ChatChannel>();
    private static readonly ChatChannel[] Defaults = [ChatChannel.General, ChatChannel.Team, ChatChannel.Guild,
        ChatChannel.Alliance, ChatChannel.Group, ChatChannel.Admin, ChatChannel.Private,
        ChatChannel.Information, ChatChannel.Fight, ChatChannel.Arena];

    internal static ChatChannelsEnabledEvent Snapshot(SessionState state)
    {
        var active = state.ActiveChatChannels ?? Defaults;
        return new(Channels.Where(active.Contains).ToArray(), Channels.Where(channel => !active.Contains(channel)).ToArray());
    }

    internal static ChannelUpdateEvent[] Updates(SessionState state) => Updates(Snapshot(state));

    private static ChannelUpdateEvent[] Updates(ChatChannelsEnabledEvent snapshot)
        => Channels.Select(channel => new ChannelUpdateEvent(snapshot.ActiveChannels.Contains(channel), channel)).ToArray();

    internal static async Task SubscribeAsync(GameSession session, ChatChannelSubscriptionRequest request)
    {
        // Validate the whole request before changing state. Disabled wins if a channel
        // occurs in both lists, matching the client's add-then-remove mask conversion.
        if (request.ActiveChannels.Concat(request.DisabledChannels).Any(channel => !Channels.Contains(channel)))
            throw new GameConversionException("Unsupported chat subscription channel.");
        session.State.ActiveChatChannels = request.ActiveChannels.Except(request.DisabledChannels).Distinct().ToArray();
        var snapshot = Snapshot(session.State);
        var updates = Updates(snapshot);
        await session.SendAsync(snapshot);
        foreach (var update in updates) await session.SendAsync(update);
    }
}
