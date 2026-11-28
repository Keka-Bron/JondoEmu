using System.Text.Json.Nodes;

namespace Jondo.Unity.Mcp;

/// <summary>The local test harness client injected into tools supplied by optional MCP providers.</summary>
public interface IMcpTestClient
{
    /// <summary>Calls a keyed route under /api/test/ for the given test account slot.</summary>
    Task<string> TestAsync(string route, JsonObject? body = null, int slot = 0, CancellationToken cancellationToken = default);

    /// <summary>Calls a route under /api/ with the test account slot's launcher token.</summary>
    Task<string> PlayerAsync(string route, JsonObject? body = null, int slot = 0, CancellationToken cancellationToken = default);
}
