using System.Runtime.CompilerServices;
using Jondo.Unity.ServerMods;

namespace Fixture;

public sealed class FixtureMod : IServerMod
{
    private IServerModHost _host = null!;

    public void Initialize(IServerModHost host)
    {
        _host = host;
        host.RegisterCleanup(() => host.Log("first resource released"));
        host.RegisterCleanup(() => host.Log("second resource released"));
        InitializeDependency(host);
    }

    public void Shutdown() => _host.Log("fixture stopped");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InitializeDependency(IServerModHost host) => host.Log(ModDependency.Message());
}
