using System.Reflection;
using System.Runtime.Loader;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Jondo.Unity.Mcp;

internal sealed class ToolProviderLoadContext(string assemblyPath) : AssemblyLoadContext(assemblyPath)
{
    private static readonly Assembly[] SharedAssemblies =
    [
        typeof(IMcpTestClient).Assembly,
        typeof(McpServerToolAttribute).Assembly,
        typeof(McpException).Assembly,
    ];

    private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

    internal static Assembly LoadProvider(string assemblyPath)
        => new ToolProviderLoadContext(assemblyPath).LoadFromAssemblyPath(assemblyPath);

    protected override Assembly? Load(AssemblyName name)
    {
        var shared = SharedAssemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(name, assembly.GetName()));
        if (shared != null) return shared;

        string? path = _resolver.ResolveAssemblyToPath(name);
        return path == null ? null : LoadFromAssemblyPath(path);
    }
}
