using System.Text.Json;

namespace Jondo.Unity.ServerMods;

public static class ServerModApi
{
    public const int Version = 2;
}

public interface IServerMod
{
    void Initialize(IServerModHost host);

    /// <summary>Starts gameplay work after the host has brought its services online.</summary>
    void StartGameplay() { }

    /// <summary>Stops a successfully initialized mod while the server is still running.</summary>
    void Shutdown() { }
}

public interface IServerModHost
{
    void Log(string message);
    JsonElement Settings { get; }

    /// <summary>Gets a host service from the shared server mod contract.</summary>
    T GetService<T>() where T : class;

    /// <summary>Registers a gameplay hook for the lifetime of this package.</summary>
    void RegisterHook<T>(T hook) where T : class;

    /// <summary>Registers cleanup for a resource, including when initialization fails.</summary>
    void RegisterCleanup(Action cleanup);

    /// <summary>Registers an exact control API path. The host resolves the account before calling the handler.</summary>
    void RegisterAuthenticatedControlRoute(string path, Func<AuthenticatedControlRequest, ServerModControlResponse> handler);
}

public readonly record struct AuthenticatedControlRequest(long AccountId, string Method, string Body);

public readonly record struct ServerModControlResponse(int StatusCode, string Json);
