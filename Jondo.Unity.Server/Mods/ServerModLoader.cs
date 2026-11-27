using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Network;
using Jondo.Unity.ServerMods;

namespace Jondo.Unity.Server.Mods;

internal sealed class ServerModLoader(Action<string> log) : IDisposable
{
    private const string ManifestFile = "manifest.json";
    private static readonly Regex ModId = new(@"\A[A-Za-z0-9][A-Za-z0-9._-]*\z", RegexOptions.CultureInvariant);
    private readonly Stack<Host> _started = new();
    private int _stopped;

    internal ServerModControlRoutes ControlRoutes { get; } = new();

    public ClientModCatalog.Asset[] ClientAssets => _started.Reverse().Select(host => host.ClientAsset)
        .OfType<ClientModCatalog.Asset>().ToArray();

    internal static ServerModLoader LoadConfigured(string directory, ServerSettings settings, Action<string> log,
        Func<string, IServerMod>? createMod = null)
        => Load(directory, settings.ServerMods, log, settings, createMod);

    public static ServerModLoader Load(string directory, string[] enabledIds, Action<string> log)
        => Load(directory, enabledIds, log, new ServerSettings(), null);

    private static ServerModLoader Load(string directory, string[] enabledIds, Action<string> log, ServerSettings settings,
        Func<string, IServerMod>? createMod)
    {
        var loader = new ServerModLoader(log);
        if (enabledIds.Length == 0) return loader;

        try
        {
            var installed = Discover(directory, enabledIds[0], log);
            foreach (string id in enabledIds.Distinct(StringComparer.OrdinalIgnoreCase))
                loader.StartMod(directory, installed, id, settings, createMod);
            return loader;
        }
        catch
        {
            loader.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        while (_started.TryPop(out var host))
            host.Stop();
    }

    internal void StartGameplay()
    {
        foreach (var host in _started.Reverse())
            host.Mod.StartGameplay();
    }

    internal static ServerModDescription[] ListInstalled(string directory)
    {
        try
        {
            return Discover(directory, "", _ => { }).Where(group => group.Count() == 1)
                .Select(group =>
                {
                    var entry = group.Single();
                    if (entry.Manifest is not { } manifest)
                        return ReferencesServerMod(entry.Path)
                            ? new ServerModDescription(group.Key, null, null, null, null, IsStandalone: true)
                            : null;
                    var schema = ServerModConfiguration.Read(manifest);
                    string? name = manifest.TryGetProperty("settings", out var settings) ? ReadString(settings, "name") : null;
                    string? asset = Path.GetFileName(ReadString(manifest, "clientAsset"));
                    return new ServerModDescription(group.Key, name, ReadString(manifest, "version"),
                        ClientModManifest.IsDllName(asset) ? asset : null, schema);
                }).OfType<ServerModDescription>().ToArray();
        }
        catch (InvalidOperationException ex) when (ex.InnerException is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // Identify loose server DLLs for the desktop without loading them into the process.
    private static bool ReferencesServerMod(string path)
    {
        try
        {
            RejectLink(path);
            using var file = File.OpenRead(path);
            using var assembly = new PEReader(file);
            if (!assembly.HasMetadata) return false;

            var metadata = assembly.GetMetadataReader();
            var strings = metadata.StringComparer;
            return metadata.TypeReferences.Select(metadata.GetTypeReference).Any(type =>
                strings.Equals(type.Name, nameof(IServerMod)) && strings.Equals(type.Namespace, typeof(IServerMod).Namespace));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or BadImageFormatException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record InstalledMod(string Path, JsonElement? Manifest);

    private static ILookup<string, InstalledMod> Discover(
        string directory, string firstEnabledId, Action<string> log)
    {
        string[] packages;
        string[] standaloneAssemblies;
        try
        {
            directory = Path.GetFullPath(directory);
            packages = Directory.GetDirectories(directory);
            standaloneAssemblies = Directory.GetFiles(directory, "*.dll",
                new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = 0 });
        }
        catch (DirectoryNotFoundException)
        {
            packages = [];
            standaloneAssemblies = [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw StartupError(firstEnabledId, ex);
        }

        var installed = new List<(string Id, InstalledMod Mod)>();
        foreach (string package in packages.Order(StringComparer.Ordinal))
        {
            try
            {
                RejectLink(package);
                using var file = File.OpenRead(RequirePackageFile(package, ManifestFile, ManifestFile));
                using var document = JsonDocument.Parse(file);
                var manifest = document.RootElement;
                if (manifest.ValueKind != JsonValueKind.Object || !manifest.TryGetProperty("id", out var property) ||
                    property.ValueKind != JsonValueKind.String || property.GetString() is not { } id || !ModId.IsMatch(id))
                    throw new InvalidDataException("Invalid or missing 'id'.");
                installed.Add((id, new InstalledMod(package, manifest.Clone())));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                ReportError(log, Path.GetFileName(package), ex);
            }
        }

        foreach (string assembly in standaloneAssemblies.Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(assembly);
            if (ModId.IsMatch(name) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                installed.Add((name, new InstalledMod(assembly, null)));
        }

        return installed.ToLookup(entry => entry.Id, entry => entry.Mod, StringComparer.OrdinalIgnoreCase);
    }

    private void StartMod(string directory, ILookup<string, InstalledMod> installedMods,
        string id, ServerSettings settings, Func<string, IServerMod>? createMod)
    {
        Host host = null;
        try
        {
            if (id == null || !ModId.IsMatch(id))
                throw new InvalidDataException("Invalid ID in the ServerMods setting.");
            var matches = installedMods[id].ToArray();
            if (matches.Length == 0)
            {
                log($"[Server mods] {id}: package not found in '{directory}'; skipped.");
                return;
            }
            if (matches.Length > 1)
                throw new InvalidDataException($"Duplicate mod ID in packages or standalone DLLs: {string.Join(", ", matches.Select(match => Path.GetFileName(match.Path)))}.");

            var installed = matches[0];
            var manifest = installed.Manifest?.Deserialize<Manifest>();
            string modId = manifest?.Id ?? Path.GetFileName(installed.Path);
            string label = manifest == null ? modId : $"{Path.GetFileName(installed.Path)} ({modId})";
            var schema = installed.Manifest is { } metadata ? ServerModConfiguration.Read(metadata) : null;
            var configuration = ServerModConfiguration.Resolve(settings, modId, schema);
            if (!ServerModConfiguration.IsActive(schema, configuration))
                return;
            host = new Host(log, label, ControlRoutes, configuration);
            string? clientPath = null;
            var mod = manifest == null
                ? LoadAssembly(Path.GetDirectoryName(installed.Path), installed.Path, null, host, modId, createMod)
                : LoadPackage(installed.Path, manifest, host, createMod, out clientPath);
            mod.Initialize(host);
            host.Mod = mod;
            if (ClientModManifest.IsDllName(Path.GetFileName(clientPath)))
            {
                string root = Path.GetDirectoryName(installed.Path);
                host.ClientAsset = new ClientModCatalog.Asset(root, Path.GetRelativePath(root, clientPath));
            }
            string version = manifest == null ? "" : $" (version {manifest.Version})";
            log($"[Server mods] {label} initialized{version}.");
            _started.Push(host);
        }
        catch (Exception ex)
        {
            host?.Stop();
            throw StartupError(id, ex);
        }
    }

    private static IServerMod LoadPackage(string package, Manifest manifest, Host host,
        Func<string, IServerMod>? createMod, out string? clientPath)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidDataException("Missing 'version'.");
        if (manifest.ServerApiVersion != ServerModApi.Version)
            throw new InvalidDataException($"Expected 'serverApiVersion' {ServerModApi.Version}, got {manifest.ServerApiVersion}.");

        string assemblyPath = RequirePackageFile(package, manifest.ServerAssembly, "serverAssembly");
        clientPath = manifest.ClientAsset == null ? null : RequirePackageFile(package, manifest.ClientAsset, "clientAsset");
        if (string.Equals(clientPath, assemblyPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("'clientAsset' cannot be the server assembly.");

        return LoadAssembly(package, assemblyPath, clientPath, host, manifest.Id, createMod);
    }

    private static IServerMod LoadAssembly(string directory, string assemblyPath, string? clientPath, Host host,
        string id, Func<string, IServerMod>? createMod)
    {
        RejectLink(assemblyPath);
        if (createMod != null) return createMod(id);

        RejectLink(Path.ChangeExtension(assemblyPath, ".deps.json"));
        var context = new PackageLoadContext(directory, assemblyPath, clientPath);
        host.RegisterCleanup(context.Unload);
        return CreateMod(context.LoadPackageAssembly(assemblyPath));
    }

    private static IServerMod CreateMod(Assembly assembly)
    {
        var entryPoints = assembly.GetExportedTypes()
            .Where(type => type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters &&
                           typeof(IServerMod).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) != null)
            .ToArray();
        if (entryPoints.Length != 1)
            throw new InvalidDataException("Expected exactly one public IServerMod implementation with a public parameterless constructor.");

        return (IServerMod)Activator.CreateInstance(entryPoints[0]);
    }

    private static string RequirePackageFile(string package, string relativePath, string field)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidDataException($"Missing '{field}'.");

        string path = package;
        foreach (string part in relativePath.Split('/', '\\'))
        {
            if (part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Contains(':'))
                throw new InvalidDataException($"'{field}' must be a package-relative file path: '{relativePath}'.");
            path = Path.Combine(path, part);
            RejectLink(path);
        }

        if (!File.Exists(path))
            throw new FileNotFoundException($"'{field}' file '{relativePath}' was not found.", path);
        return path;
    }

    private static void RejectLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Links are not supported in server mod packages: '{path}'.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static Exception StartupError(string id, Exception error)
    {
        if (error is TargetInvocationException { InnerException: { } cause })
            error = cause;
        return new InvalidOperationException($"Server mod '{id ?? "(null)"}' failed to start: {error.Message}", error);
    }

    private static void ReportError(Action<string> log, string package, Exception error)
        => log($"[Server mods] {package} error: {error.Message}");

    private sealed record Manifest(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("serverApiVersion")] int ServerApiVersion,
        [property: JsonPropertyName("serverAssembly")] string ServerAssembly,
        [property: JsonPropertyName("clientAsset")] string ClientAsset);

    private sealed class Host(Action<string> log, string label, ServerModControlRoutes controlRoutes, JsonElement settings)
        : IServerModHost
    {
        private readonly Stack<Action> _cleanup = new();
        private bool _stopping;
        internal IServerMod Mod { get; set; }
        internal ClientModCatalog.Asset? ClientAsset { get; set; }

        public void Log(string message) => log($"[Server mods] {label}: {message}");

        public void RegisterAuthenticatedControlRoute(string path, Func<AuthenticatedControlRequest, ServerModControlResponse> handler)
        {
            lock (_cleanup)
            {
                if (_stopping) throw new InvalidOperationException("The server mod is stopping.");
                controlRoutes.Add(path, handler);
                _cleanup.Push(() => controlRoutes.Remove(path));
            }
        }

        private static readonly CharacterWorld Characters = new();
        private static readonly AlignmentWorld Alignment = new();
        private static readonly Dictionary<Type, object> Services = new()
        {
            [typeof(ICharacterWorld)] = Characters,
            [typeof(ICompanionWorld)] = Characters,
            [typeof(IPartyWorld)] = Characters,
            [typeof(IMovementWorld)] = Characters,
            [typeof(IQuestWorld)] = Characters,
            [typeof(IAlignmentWorld)] = Alignment,
            [typeof(IWorldStorage)] = Alignment,
            [typeof(ISimulatedPlayerWorld)] = Alignment,
        };

        public JsonElement Settings => settings;

        public T GetService<T>() where T : class => Services.TryGetValue(typeof(T), out var service)
            ? (T)service
            : throw new InvalidOperationException($"Unknown server mod service '{typeof(T).FullName}'.");

        public void RegisterHook<T>(T hook) where T : class
        {
            if (!typeof(T).IsInterface || typeof(T).Assembly != typeof(IServerMod).Assembly)
                throw new ArgumentException("Hooks must implement a shared server mod contract interface.", nameof(hook));

            lock (_cleanup)
            {
                if (_stopping) throw new InvalidOperationException("The server mod is stopping.");
                _cleanup.Push(ServerModHooks.Register(hook));
            }
        }

        public void RegisterCleanup(Action cleanup)
        {
            ArgumentNullException.ThrowIfNull(cleanup);
            lock (_cleanup)
            {
                if (_stopping) throw new InvalidOperationException("The server mod is stopping.");
                _cleanup.Push(cleanup);
            }
        }

        internal void Stop()
        {
            lock (_cleanup) _stopping = true;
            if (Mod is { } mod) RunCleanup(mod.Shutdown);
            Mod = null;
            while (_cleanup.TryPop(out var cleanup))
                RunCleanup(cleanup);
        }

        private void RunCleanup(Action cleanup)
        {
            try { cleanup(); }
            catch (Exception ex) { ReportError(log, label, ex); }
        }
    }

    private sealed class PackageLoadContext(string package, string assemblyPath, string? clientPath) : AssemblyLoadContext(isCollectible: true)
    {
        private static readonly Assembly ContractAssembly = typeof(IServerMod).Assembly;
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        public Assembly LoadPackageAssembly(string path)
        {
            // Path-based loading holds the package DLL open on Windows.
            using var stream = File.OpenRead(path);
            return LoadFromStream(stream);
        }

        protected override Assembly Load(AssemblyName name)
        {
            if (string.Equals(name.Name, ContractAssembly.GetName().Name, StringComparison.OrdinalIgnoreCase))
                return ContractAssembly;

            string path = _resolver.ResolveAssemblyToPath(name);
            if (path == null && string.IsNullOrEmpty(name.CultureName))
            {
                string adjacent = Path.Combine(Path.GetDirectoryName(assemblyPath), name.Name + ".dll");
                if (Path.Exists(adjacent))
                    path = adjacent;
            }

            if (path == null) return null;
            path = RequirePackageFile(package, Path.GetRelativePath(package, path), $"dependency {name.Name}");
            if (string.Equals(path, clientPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("'clientAsset' cannot be a private server dependency.");
            return LoadPackageAssembly(path);
        }
    }
}

internal sealed record ServerModDescription(string Id, string? Name, string? Version, string? ClientAsset,
    ServerModConfiguration? Configuration, bool IsStandalone = false);
