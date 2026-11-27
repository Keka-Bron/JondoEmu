using System.Collections.Immutable;
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

internal sealed class ServerModLoader(Action<string> log, bool failFast,
    Action<ImmutableDictionary<string, ServerModStartupIssue>>? publishIssues) : IDisposable
{
    private const string ManifestFile = "manifest.json";
    private static readonly Regex ModId = new(@"\A[A-Za-z0-9][A-Za-z0-9._-]*\z", RegexOptions.CultureInvariant);
    private static readonly StringComparer Ids = StringComparer.OrdinalIgnoreCase;
    private readonly List<Host> _started = [];
    private int _stopped;

    internal ServerModControlRoutes ControlRoutes { get; } = new();
    internal ImmutableDictionary<string, ServerModStartupIssue> StartupIssues { get; private set; } =
        ImmutableDictionary.Create<string, ServerModStartupIssue>(Ids);

    public ClientModCatalog.Asset[] ClientAssets => _started.Select(host => host.ClientAsset)
        .OfType<ClientModCatalog.Asset>().ToArray();

    internal static ServerModLoader LoadConfigured(string directory, ServerSettings settings, Action<string> log,
        Func<string, IServerMod>? createMod = null,
        Action<ImmutableDictionary<string, ServerModStartupIssue>>? publishIssues = null)
        => Load(directory, settings, log, createMod, publishIssues);

    public static ServerModLoader Load(string directory, string[] enabledIds, Action<string> log)
        => LoadConfigured(directory, new ServerSettings { ServerMods = enabledIds }, log);

    private static ServerModLoader Load(string directory, ServerSettings settings, Action<string> log,
        Func<string, IServerMod>? createMod, Action<ImmutableDictionary<string, ServerModStartupIssue>>? publishIssues)
    {
        var loader = new ServerModLoader(log, settings.FailOnServerModError, publishIssues);
        var enabledIds = settings.ServerMods.Distinct(Ids).ToArray();
        if (enabledIds.Length == 0) return loader;

        try
        {
            ILookup<string, InstalledMod> installed;
            try { installed = Discover(directory, log); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                string message = $"Could not read mods directory '{directory}': {ex.Message}";
                log($"[Server mods] {message}");
                foreach (string id in enabledIds)
                    loader.RecordIssue(id, skipped: false, message);
                if (settings.FailOnServerModError) throw StartupError(enabledIds[0], ex);
                return loader;
            }

            var pending = enabledIds.ToHashSet(Ids);
            var initialized = new HashSet<string>(Ids);
            foreach (string id in enabledIds) Start(id);
            return loader;

            bool Start(string id)
            {
                if (pending.Remove(id) && loader.StartMod(directory, installed, id, settings, createMod, Start))
                    initialized.Add(id);
                return initialized.Contains(id);
            }
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
        for (int i = _started.Count - 1; i >= 0; i--) _started[i].Stop();
        _started.Clear();
    }

    internal void StartGameplay()
    {
        for (int i = 0; i < _started.Count; i++)
        {
            var host = _started[i];
            try { host.Mod.StartGameplay(); }
            catch (Exception ex)
            {
                HandleFailure(host.Id, ex);
                var stopped = new HashSet<string>(Ids) { host.Id };
                for (int j = i + 1; j < _started.Count; j++)
                {
                    var dependent = _started[j];
                    if (dependent.Dependencies.FirstOrDefault(stopped.Contains) is not { } dependency) continue;
                    stopped.Add(dependent.Id);
                    SkipDependency(dependent.Id, dependency, "start gameplay");
                }
                for (int j = _started.Count - 1; j >= i; j--)
                {
                    if (!stopped.Contains(_started[j].Id)) continue;
                    _started[j].Stop();
                    _started.RemoveAt(j);
                }
                i--;
            }
        }
    }

    internal static ServerModDescription[] ListInstalled(string directory)
    {
        try
        {
            return Discover(directory, _ => { }).Where(group => group.Count() == 1)
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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

    private static ILookup<string, InstalledMod> Discover(string directory, Action<string> log)
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

    private bool StartMod(string directory, ILookup<string, InstalledMod> installedMods,
        string id, ServerSettings settings, Func<string, IServerMod>? createMod, Func<string, bool> startDependency)
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
                return false;
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
                return false;
            string[] dependencies = manifest?.Dependencies ?? [];
            if (dependencies.Any(dependency => dependency == null || !ModId.IsMatch(dependency)))
                throw new InvalidDataException("Invalid mod ID in 'dependencies'.");
            if (dependencies.FirstOrDefault(dependency => !startDependency(dependency)) is { } unavailable)
            {
                SkipDependency(id, unavailable, "initialize");
                return false;
            }
            host = new Host(log, label, ControlRoutes, configuration, modId, dependencies);
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
            _started.Add(host);
            return true;
        }
        catch (Exception ex) when (ex is not ModStartupException)
        {
            host?.Stop();
            HandleFailure(id, ex);
            return false;
        }
    }

    private void HandleFailure(string id, Exception error)
    {
        var failure = StartupError(id, error);
        RecordIssue(id, skipped: false, failure.InnerException!.Message);
        if (failFast) throw failure;
        log($"[Server mods] {failure}");
    }

    private void SkipDependency(string id, string dependency, string phase)
    {
        string message = $"Required mod '{dependency}' did not {phase}.";
        RecordIssue(id, skipped: true, message);
        log($"[Server mods] {id}: {message} Skipped.");
    }

    private void RecordIssue(string id, bool skipped, string message)
    {
        if (id == null) return;
        StartupIssues = StartupIssues.SetItem(id, new(skipped, message));
        publishIssues?.Invoke(StartupIssues);
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

    private static ModStartupException StartupError(string id, Exception error)
    {
        if (error is TargetInvocationException { InnerException: { } cause })
            error = cause;
        return new ModStartupException($"Server mod '{id ?? "(null)"}' failed to start: {error.Message}", error);
    }

    private sealed class ModStartupException(string message, Exception cause) : InvalidOperationException(message, cause);

    private static void ReportError(Action<string> log, string package, Exception error)
        => log($"[Server mods] {package} error: {error.Message}");

    private sealed record Manifest(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("serverApiVersion")] int ServerApiVersion,
        [property: JsonPropertyName("serverAssembly")] string ServerAssembly,
        [property: JsonPropertyName("clientAsset")] string ClientAsset,
        [property: JsonPropertyName("dependencies")] string[]? Dependencies);

    private sealed class Host(Action<string> log, string label, ServerModControlRoutes controlRoutes, JsonElement settings,
        string id, string[] dependencies)
        : IServerModHost
    {
        private readonly Stack<Action> _cleanup = new();
        private bool _stopping;
        internal IServerMod Mod { get; set; }
        internal ClientModCatalog.Asset? ClientAsset { get; set; }
        internal string Id => id;
        internal string[] Dependencies => dependencies;

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

internal sealed record ServerModStartupIssue(bool Skipped, string Message);
