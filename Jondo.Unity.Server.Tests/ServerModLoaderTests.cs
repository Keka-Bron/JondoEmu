using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Mods;
using Jondo.Unity.Server.Network;
using Jondo.Unity.ServerMods;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class ServerModLoaderTests : IDisposable
{
    private const string AssemblyFile = "Fixture.ServerMod.dll";
    private const string DependencyFile = "Fixture.ServerModDependency.dll";
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory();
    private readonly List<string> _messages = [];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InitializesExternalAssemblyWithSharedContractAndPrivateDependency(bool includeDepsFile)
    {
        var package = CreatePackage("external", serverAssembly: $"lib/{AssemblyFile}", clientAsset: "client.dat");
        string lib = Directory.CreateDirectory(Path.Combine(package, "lib")).FullName;
        CopyFixture(AssemblyFile, lib);
        CopyFixture(DependencyFile, lib);
        if (includeDepsFile)
            CopyFixture("Fixture.ServerMod.deps.json", lib);
        string hostContract = typeof(IServerMod).Assembly.Location;
        File.Copy(hostContract, Path.Combine(lib, Path.GetFileName(hostContract)));
        File.WriteAllText(Path.Combine(package, "client.dat"), "fixture client asset");
        CreatePackage("broken");

        using var mods = ServerModLoader.Load(_root.FullName, ["Fixture.External"], _messages.Add);

        Assert.Empty(mods.ClientAssets);
        Assert.Contains("[Server mods] external (fixture.external): fixture initialized through its private dependency", _messages);
        Assert.Contains("[Server mods] external (fixture.external) initialized (version 1.0.0).", _messages);
        Assert.DoesNotContain(_messages, message => message.Contains("fixture.broken"));
        Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies,
            assembly => assembly.GetName().Name is "Fixture.ServerMod" or "Fixture.ServerModDependency");
        Assert.DoesNotContain(typeof(ServerModLoader).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name is "Fixture.ServerMod" or "Fixture.ServerModDependency" or "Cazacabezas" or "Cazacabezas.Server" or "Heroes" or "Heroes.Server");
    }

    [Fact]
    public async Task DroppedDllLoadsWithPackagesInConfiguredOrderAfterRestart()
    {
        const string file = "Dropped.DLL";
        InstallFixture("package");
        string path = Path.Combine(_root.FullName, file);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "ServerModFixture", AssemblyFile), path);
        CopyFixture(DependencyFile, _root.FullName);
        File.WriteAllBytes(Path.Combine(_root.FullName, "JondoFix.dll"), [1]);
        var installed = ServerModLoader.ListInstalled(_root.FullName);
        Assert.Contains(installed, mod => mod.Id == file && mod.IsStandalone);
        Assert.DoesNotContain(installed, mod => mod.Id is "JondoFix.dll" or DependencyFile);

        Task Start(string json) => Program.RunEmulationAsync(_root.FullName, ServerSettings.Read(json),
            new TaskCompletionSource().Task, (mods, catalog) =>
            {
                Assert.Empty(mods.ClientAssets);
                Assert.Equal("JondoFix.dll", Assert.Single(catalog.CreateManifest().Mods).Name);
                _messages.Add("ready");
                return Task.CompletedTask;
            }, () => { }, _messages.Add);

        await Start("""{"ServerMods":["fixture.package"]}""");
        Assert.DoesNotContain(_messages, message => message.Contains(file));
        _messages.Clear();

        await Start("""{"ServerMods":["dropped.dll","fixture.package","DROPPED.DLL"]}""");
        Assert.Equal([
            "[Server mods] Dropped.DLL initialized.",
            "[Server mods] package (fixture.package) initialized (version 1.0.0).",
            "ready"
        ], _messages.Where(message => message.EndsWith("initialized.") || message.Contains("initialized (version") || message == "ready"));
        Assert.Contains("[Server mods] Dropped.DLL: fixture stopped", _messages);
        File.Delete(path);
    }

    [Fact]
    public void RejectsIdSharedByPackageAndStandaloneDll()
    {
        CreatePackage("package", id: "fixture.servermod.dll");
        CopyFixture(AssemblyFile, _root.FullName);

        Assert.Empty(ServerModLoader.ListInstalled(_root.FullName));
        var failure = Assert.Throws<InvalidOperationException>(() =>
            ServerModLoader.Load(_root.FullName, [AssemblyFile], _messages.Add));
        Assert.Contains("Duplicate mod ID", failure.Message);
    }

    [Fact]
    public async Task ClientEndpointsPublishOnlyActiveDeclaredDllsAndConfiguredStandaloneMods()
    {
        File.WriteAllBytes(Path.Combine(_root.FullName, "JondoFix.dll"), [1]);
        File.WriteAllBytes(Path.Combine(_root.FullName, "Shared.dll"), [2]);
        File.WriteAllBytes(Path.Combine(_root.FullName, "Standalone.dll"), [3]);
        InstallFixture("z-first", "client/Shared.dll", [4, 5]);
        InstallFixture("a-second", "client/shared.dll", [6]);
        InstallFixture("extra", "client/Extra.dll", [7]);
        InstallFixture("disabled", "client/Disabled.dll", [8]);
        InstallFixture("server-only");
        var installed = Assert.Single(ServerModLoader.ListInstalled(_root.FullName), mod => mod.Id == "fixture.z-first");
        Assert.Equal("1.0.0", installed.Version);
        Assert.Equal("Shared.dll", installed.ClientAsset);
        string[] standalone = ["JondoFix.dll", "Shared.dll", "Standalone.dll", AssemblyFile];

        using var mods = ServerModLoader.Load(_root.FullName,
            ["fixture.z-first", "fixture.a-second", "fixture.extra", "fixture.server-only"], _messages.Add);
        var catalog = new ClientModCatalog(_root.FullName, standalone, mods.ClientAssets);
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        using var listener = new HttpListener();
        string address = $"http://localhost:{port}/";
        listener.Prefixes.Add(address);
        listener.Start();
        using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };

        async Task<HttpResponseMessage> Get(string path)
        {
            Task<HttpResponseMessage> pending = http.GetAsync(path);
            var request = await listener.GetContextAsync().WaitAsync(http.Timeout);
            await HaapiServer.HandleHaapiRequestAsync(request, catalog).WaitAsync(http.Timeout);
            return await pending;
        }

        string route = ClientModManifest.Route;
        using var response = await Get(route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var manifest = (await response.Content.ReadFromJsonAsync<ClientModManifest>())!;
        Assert.Equal(["JondoFix.dll", "Shared.dll", "Standalone.dll", "Extra.dll"], manifest.Mods.Select(mod => mod.Name));

        using var download = await Get(route + "/Shared.dll");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.True(download.Headers.CacheControl?.NoStore);
        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType?.MediaType);
        byte[] bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 4, 5 }, bytes);
        Assert.Equal(bytes.Length, manifest.Mods[1].Size);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), manifest.Mods[1].Sha256);

        using var fix = await Get(route + "/JondoFix.dll");
        Assert.Equal(HttpStatusCode.OK, fix.StatusCode);
        Assert.Equal(new byte[] { 1 }, await fix.Content.ReadAsByteArrayAsync());
        using var extra = await Get(route + "/Extra.dll");
        Assert.Equal(HttpStatusCode.OK, extra.StatusCode);
        Assert.Equal(new byte[] { 7 }, await extra.Content.ReadAsByteArrayAsync());

        foreach (string name in new[] { AssemblyFile, DependencyFile, "manifest.json", "client/Extra.dll", "shared.dll", "Disabled.dll" })
        {
            using var rejected = await Get(route + "/" + name);
            Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
        }

        File.Delete(Path.Combine(_root.FullName, "z-first", "client", "Shared.dll"));
        using var unavailable = await Get(route + "/Shared.dll");
        Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
        Assert.DoesNotContain(catalog.CreateManifest().Mods, mod => mod.Name.Equals("Shared.dll", StringComparison.OrdinalIgnoreCase));

        using var disabled = ServerModLoader.Load(_root.FullName, [], _messages.Add);
        var legacy = new ClientModCatalog(_root.FullName, standalone, disabled.ClientAssets);
        using var fallback = legacy.OpenDownload("Shared.dll");
        Assert.NotNull(fallback);
        Assert.Equal(2, fallback.ReadByte());
    }

    [Fact]
    public void RejectsClientAssetUsedAsPrivateServerDependency()
    {
        string package = CreatePackage("private", clientAsset: DependencyFile);
        CopyFixture(AssemblyFile, package);
        CopyFixture(DependencyFile, package);

        var failure = Assert.Throws<InvalidOperationException>(() =>
            ServerModLoader.Load(_root.FullName, ["fixture.private"], _messages.Add));
        Assert.Contains("'clientAsset' cannot be a private server dependency", failure.ToString());
    }

    [Fact]
    public void RejectsAllPackagesWithTheSameIdIgnoringCase()
    {
        CreatePackage("first", id: "fixture.duplicate");
        CreatePackage("second", id: "Fixture.Duplicate");

        var failure = Assert.Throws<InvalidOperationException>(() =>
            ServerModLoader.Load(_root.FullName, ["fixture.duplicate"], _messages.Add));
        Assert.Contains("Server mod 'fixture.duplicate' failed to start: Duplicate mod ID", failure.Message);
        Assert.Contains("first, second", failure.Message);
    }

    [Theory]
    [InlineData(ServerModApi.Version - 1, AssemblyFile, null, "Expected 'serverApiVersion'")]
    [InlineData(ServerModApi.Version, "missing.dll", null, "'serverAssembly' file 'missing.dll' was not found")]
    [InlineData(ServerModApi.Version, AssemblyFile, "missing.dat", "'clientAsset' file 'missing.dat' was not found")]
    [InlineData(ServerModApi.Version, "../outside.dll", null, "'serverAssembly' must be a package-relative file path")]
    [InlineData(ServerModApi.Version, "C:\\outside.dll", null, "'serverAssembly' must be a package-relative file path")]
    [InlineData(ServerModApi.Version, AssemblyFile, "..\\outside.dat", "'clientAsset' must be a package-relative file path")]
    [InlineData(ServerModApi.Version, AssemblyFile, AssemblyFile, "'clientAsset' cannot be the server assembly")]
    public void RejectsInvalidPackages(int api, string serverAssembly, string? clientAsset, string error)
    {
        string package = CreatePackage("bad", api: api, serverAssembly: serverAssembly, clientAsset: clientAsset);
        CopyFixture(AssemblyFile, package);

        var failure = Assert.Throws<InvalidOperationException>(() =>
            ServerModLoader.Load(_root.FullName, ["fixture.bad"], _messages.Add));
        Assert.StartsWith($"Server mod 'fixture.bad' failed to start: {error}", failure.Message);
    }

    [Fact]
    public void ReportsMalformedManifestAndMissingEntryPoint()
    {
        string malformed = Directory.CreateDirectory(Path.Combine(_root.FullName, "bad-json")).FullName;
        File.WriteAllText(Path.Combine(malformed, "manifest.json"), "{");
        string package = CreatePackage("no-entry", serverAssembly: DependencyFile);
        CopyFixture(DependencyFile, package);

        var failure = Assert.Throws<InvalidOperationException>(() =>
            ServerModLoader.Load(_root.FullName, ["fixture.no-entry"], _messages.Add));
        Assert.StartsWith("[Server mods] bad-json error:", Assert.Single(_messages));
        Assert.Contains("Server mod 'fixture.no-entry' failed to start: Expected exactly one public IServerMod", failure.Message);
    }

    [Fact]
    public void ReportsMissingPrivateDependencyWithModId()
    {
        string package = CreatePackage("missing-dependency");
        CopyFixture(AssemblyFile, package);

        var failure = Assert.Throws<InvalidOperationException>(() =>
            ServerModLoader.Load(_root.FullName, ["fixture.missing-dependency"], _messages.Add));
        Assert.StartsWith("Server mod 'fixture.missing-dependency' failed to start:", failure.Message);
        Assert.Contains("Fixture.ServerModDependency", failure.Message);
    }

    [Fact]
    public async Task DefaultSettingsStartWithoutPackagesOrModRoutes()
    {
        string missingRoot = Path.Combine(_root.FullName, "mods");
        await Program.RunEmulationAsync(missingRoot, ServerSettings.Read("{}"),
            new TaskCompletionSource().Task, (mods, catalog) =>
            {
                Assert.False(mods.ControlRoutes.TryGet("/api/fixture/status", out _));
                Assert.Empty(mods.ClientAssets);
                Assert.Empty(catalog.CreateManifest().Mods);
                _messages.Add("ready");
                return Task.CompletedTask;
            }, () => _messages.Add("host stopped"), _messages.Add);

        Assert.Equal(["ready", "host stopped"], _messages);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MissingConfiguredPackagesDoNotPreventAdmission(bool firstPresent, bool secondPresent)
    {
        var expected = new List<string>();
        if (firstPresent)
        {
            InstallFixture("first", "First.dll", [1]);
            expected.Add("First.dll");
        }
        if (secondPresent)
        {
            InstallFixture("second", "Second.dll", [2]);
            expected.Add("Second.dll");
        }

        await Program.RunEmulationAsync(_root.FullName,
            new ServerSettings { ServerMods = ["fixture.first", "fixture.second"] },
            new TaskCompletionSource().Task, (mods, catalog) =>
            {
                Assert.Equal(expected, mods.ClientAssets.Select(asset => Path.GetFileName(asset.RelativePath)));
                Assert.Equal(expected, catalog.CreateManifest().Mods.Select(mod => mod.Name));
                _messages.Add("ready");
                return Task.CompletedTask;
            }, () => _messages.Add("host stopped"), _messages.Add);

        Assert.Contains("ready", _messages);
        Assert.Equal("host stopped", _messages[^1]);
        Assert.Equal(!firstPresent, _messages.Any(message => message.Contains("fixture.first: package not found")));
        Assert.Equal(!secondPresent, _messages.Any(message => message.Contains("fixture.second: package not found")));
    }

    [Fact]
    public async Task EnabledPackagesStartBeforeAdmissionAndStopOnceBeforeHostTeardown()
    {
        InstallFixture("z-first");
        InstallFixture("a-second");
        InstallFixture("disabled");
        var settings = ServerSettings.Read("""{"ServerMods":["fixture.z-first","FIXTURE.A-SECOND"]}""");
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task run = Program.RunEmulationAsync(_root.FullName, settings, shutdown.Task, (_, _) =>
        {
            _messages.Add("ready");
            return shutdown.Task;
        }, () => _messages.Add("host stopped"), _messages.Add);

        Assert.Equal([
            "[Server mods] z-first (fixture.z-first): fixture initialized through its private dependency",
            "[Server mods] z-first (fixture.z-first) initialized (version 1.0.0).",
            "[Server mods] a-second (fixture.a-second): fixture initialized through its private dependency",
            "[Server mods] a-second (fixture.a-second) initialized (version 1.0.0).",
            "ready"
        ], _messages);

        Parallel.For(0, 8, _ => shutdown.TrySetResult());
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([
            "[Server mods] a-second (fixture.a-second): fixture stopped",
            "[Server mods] a-second (fixture.a-second): second resource released",
            "[Server mods] a-second (fixture.a-second): first resource released",
            "[Server mods] z-first (fixture.z-first): fixture stopped",
            "[Server mods] z-first (fixture.z-first): second resource released",
            "[Server mods] z-first (fixture.z-first): first resource released",
            "host stopped"
        ], _messages.Skip(5));
    }

    [Fact]
    public async Task PartialStartupRollsBackBeforeHostTeardownAndUsesStartupFailureFormat()
    {
        InstallFixture("first");
        string broken = CreatePackage("broken");
        CopyFixture(AssemblyFile, broken);
        InstallFixture("later");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Program.RunEmulationAsync(_root.FullName,
                new ServerSettings { ServerMods = ["fixture.first", "fixture.broken", "fixture.later"] },
                new TaskCompletionSource().Task, (_, _) => throw new Xunit.Sdk.XunitException("Listeners started"),
                () => _messages.Add("host stopped"), _messages.Add));

        string failure = Program.StartupFailure(exception);
        Assert.Contains("Fatal error while starting Jondo Server", failure);
        Assert.Contains("Server mod 'fixture.broken' failed to start:", failure);
        Assert.Contains("Fixture.ServerModDependency", failure);
        Assert.Equal([
            "[Server mods] first (fixture.first): fixture initialized through its private dependency",
            "[Server mods] first (fixture.first) initialized (version 1.0.0).",
            "[Server mods] broken (fixture.broken): second resource released",
            "[Server mods] broken (fixture.broken): first resource released",
            "[Server mods] first (fixture.first): fixture stopped",
            "[Server mods] first (fixture.first): second resource released",
            "[Server mods] first (fixture.first): first resource released",
            "host stopped"
        ], _messages);
    }

    [Fact]
    public async Task ShutdownDuringPluginInitializationSkipsAdmissionAndCleansUp()
    {
        InstallFixture("first");
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Program.RunEmulationAsync(_root.FullName,
            new ServerSettings { ServerMods = ["fixture.first"] }, shutdown.Task,
            (_, _) => throw new Xunit.Sdk.XunitException("Listeners started"), () => _messages.Add("host stopped"),
            message =>
            {
                _messages.Add(message);
                if (message.Contains("private dependency")) shutdown.TrySetResult();
            });

        Assert.Equal("[Server mods] first (fixture.first): fixture stopped", _messages[2]);
        Assert.Equal("host stopped", _messages[^1]);
    }

    [Fact]
    public void SettingsCopyAndSavePreserveEnabledIdsAndTrackDifferences()
    {
        Assert.Empty(ServerSettings.Read("""{"ServerMods":null}""").ServerMods);
        var running = ServerSettings.Read("""{"ServerMods":["fixture.running"]}""");
        var edited = running.Copy();
        edited.ServerMods[0] = "fixture.next";
        Assert.Equal(["fixture.running"], running.ServerMods);
        Assert.Equal(["fixture.next"], ServerSettings.Read(JsonSerializer.Serialize(edited)).ServerMods);
        Assert.True(edited.DiffersFrom(running));
    }

    [Theory]
    [InlineData("""{"LegacyActive":true,"LegacyDelay":9}""", null)]
    [InlineData("""{"ServerMods":["fixture.configuration"],"LegacyActive":true,"LegacyDelay":9}""", 9)]
    [InlineData("""{"ServerMods":["fixture.configuration"],"LegacyActive":false,"LegacyDelay":9}""", null)]
    [InlineData("""{"ServerMods":["fixture.configuration"],"LegacyActive":false,"LegacyDelay":9,"Fixture.Configuration":{"Enabled":true,"Delay":12}}""", 12)]
    [InlineData("""{"ServerMods":["fixture.configuration"],"LegacyActive":true,"fixture.configuration":{"Enabled":false,"Delay":12}}""", null)]
    public void PackageSettingsRespectSelectionActivationAndNestedOverrides(string json, int? expectedDelay)
    {
        var schema = new
        {
            activationSetting = "Enabled",
            fields = new object[]
            {
                new { key = "Enabled", legacyKey = "LegacyActive", @default = true },
                new { key = "Delay", legacyKey = "LegacyDelay", @default = 3 },
            }
        };
        var package = CreatePackage("configuration", clientAsset: "Fixture.Client.dll", settings: schema);
        CopyFixture(AssemblyFile, package);
        File.WriteAllBytes(Path.Combine(package, "Fixture.Client.dll"), [1]);
        var settings = ServerSettings.Read(json);
        var fixture = new ConfigurableFixture();
        using var mods = ServerModLoader.LoadConfigured(_root.FullName, settings, _messages.Add, _ => fixture);

        Assert.Equal(expectedDelay, fixture.Delay);
        Assert.Equal(expectedDelay.HasValue, mods.ControlRoutes.TryGet(ConfigurableFixture.StatusRoute, out _));
        Assert.Equal(expectedDelay.HasValue ? 1 : 0, mods.ClientAssets.Length);
    }

    private sealed class ConfigurableFixture : IServerMod
    {
        internal const string StatusRoute = "/api/fixture/status";
        internal int? Delay { get; private set; }

        public void Initialize(IServerModHost host)
        {
            Delay = host.Settings.GetProperty("Delay").GetInt32();
            host.RegisterAuthenticatedControlRoute(StatusRoute, _ => new(200, "{}"));
        }
    }

    private void InstallFixture(string folder, string? clientAsset = null, byte[]? bytes = null)
    {
        string package = CreatePackage(folder, clientAsset: clientAsset);
        CopyFixture(AssemblyFile, package);
        CopyFixture(DependencyFile, package);
        if (clientAsset != null)
        {
            string path = Path.Combine(package, clientAsset);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes!);
        }
    }

    private string CreatePackage(string name, string? id = null, int api = ServerModApi.Version,
        string serverAssembly = AssemblyFile, string? clientAsset = null, object? settings = null)
    {
        var package = Directory.CreateDirectory(Path.Combine(_root.FullName, name));
        File.WriteAllText(Path.Combine(package.FullName, "manifest.json"), JsonSerializer.Serialize(new
        {
            id = id ?? $"fixture.{name}",
            version = "1.0.0",
            serverApiVersion = api,
            serverAssembly,
            clientAsset,
            settings
        }));
        return package.FullName;
    }

    private static void CopyFixture(string file, string directory) =>
        File.Copy(Path.Combine(AppContext.BaseDirectory, "ServerModFixture", file), Path.Combine(directory, file));

    public void Dispose() => _root.Delete(recursive: true);
}
