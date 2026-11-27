using Heroes.Server;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Mods;

namespace Jondo.Unity.Tests.World;

internal sealed class HeroesTestPackage : IDisposable
{
    private readonly HeroesMod _mod = new();
    private readonly ServerModLoader _loader;

    internal static string Directory => Path.Combine(AppContext.BaseDirectory, "ServerModFixtures");

    internal HeroesTestPackage()
    {
        var settings = new ServerSettings { ServerMods = ["heroes"] };
        _loader = ServerModLoader.LoadConfigured(Directory, settings, Console.WriteLine, _ => _mod);
    }

    internal HeroGroups Groups => _mod.Groups ?? throw new InvalidOperationException("Heroes did not start.");

    public void Dispose() => _loader.Dispose();
}
