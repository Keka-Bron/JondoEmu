using Jondo.Unity.Protocol.Game;

namespace Jondo.Unity.Server.Network;

/// <summary>The shared welcome and map-entry clock policy.</summary>
internal static class ServerTimeMessages
{
    internal const int TimezoneOffsetMinutes = 120;

    internal static TimeEvent Snapshot(TimeProvider? clock = null)
        => new(TimezoneOffsetMinutes, (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds());
}
