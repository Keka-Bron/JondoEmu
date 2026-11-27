using System.Collections.Concurrent;
using Jondo.Unity.Launcher;
using Jondo.Unity.ServerMods;

namespace Jondo.Unity.Server.Mods;

internal sealed class ServerModControlRoutes
{
    private readonly ConcurrentDictionary<string, Func<AuthenticatedControlRequest, ServerModControlResponse>> _handlers =
        new(StringComparer.Ordinal);

    internal void Add(string path, Func<AuthenticatedControlRequest, ServerModControlResponse> handler)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(handler);
        if (!path.StartsWith(Contract.Prefijo, StringComparison.Ordinal) || path.Length == Contract.Prefijo.Length ||
            path.Contains('?') || path.Contains('#') ||
            path.StartsWith(ClientModManifest.Route + "/", StringComparison.Ordinal))
            throw new ArgumentException($"Invalid server mod control path: '{path}'.", nameof(path));

        if (!_handlers.TryAdd(path, handler))
            throw new InvalidOperationException($"A server mod control handler is already registered for '{path}'.");
    }

    internal bool TryGet(string path, out Func<AuthenticatedControlRequest, ServerModControlResponse> handler)
        => _handlers.TryGetValue(path, out handler);

    internal void Remove(string path) => _handlers.TryRemove(path, out _);
}
