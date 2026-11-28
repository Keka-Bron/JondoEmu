using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using Jondo.Unity.Launcher;
using Xunit;

namespace Jondo.Unity.Tests.Launcher;

public sealed class ClientModSyncTests : IDisposable
{
    private static readonly Uri Server = new("http://mods.test:8888");
    private readonly string _cache = Path.Combine(Path.GetTempPath(), $"jondo-mod-sync-{Guid.NewGuid():N}");

    [Fact]
    public void An_approved_manifest_downloads_missing_blobs_and_reuses_them_after_reordering()
    {
        byte[] firstDll = [1, 2, 3];
        byte[] secondDll = [4, 5, 6];
        ClientModEntry first = Entry("First.dll", firstDll);
        ClientModEntry second = Entry("Second.dll", secondDll);
        using var feed = new Feed(first, second);
        feed.Blobs.Add(first.Name, firstDll);
        feed.Blobs.Add(second.Name, secondDll);
        using var http = new HttpClient(feed);

        var firstPlan = Prepare(http, "", (host, manifest, fingerprint) =>
        {
            Assert.Equal("mods.test", host);
            Assert.Equal(new[] { first, second }, manifest.Mods);
            Assert.Equal(64, fingerprint.Length);
            Assert.Empty(feed.Downloads);
            return true;
        });
        Assert.Equal(new[] { first.Name, second.Name }, feed.Downloads);
        Assert.Equal(new[] { first, second }, Read(firstPlan).Mods);

        feed.Manifest = new ClientModManifest([second, first]);
        feed.Downloads.Clear();
        var secondPlan = Prepare(http, firstPlan.NewApproval, (_, _, _) => true);
        Assert.Empty(feed.Downloads);
        Assert.NotEqual(firstPlan.ManifestPath, secondPlan.ManifestPath);
        Assert.Equal(new[] { second, first }, Read(secondPlan).Mods);
        Assert.Equal(new[] { first, second }, Read(firstPlan).Mods);
    }

    [Fact]
    public void A_declined_manifest_does_not_download_or_publish_any_dlls()
    {
        using var feed = new Feed(Entry("Untrusted.dll", [1]));
        using var http = new HttpClient(feed);
        Assert.Throws<ClientModDeployment.ApprovalDeclinedException>(() => Prepare(http, "", (_, _, _) => false));
        Assert.Empty(feed.Downloads);
        Assert.False(Directory.Exists(_cache));
    }

    [Fact]
    public void A_native_modded_launch_installs_the_bootstrap_and_passes_the_manifest_path()
    {
        string shippedFix = ReleaseFix();
        string bundle = Path.Combine(_cache, "bundle");
        string bootstrap = Path.Combine(Directory.CreateDirectory(Path.Combine(bundle, "JondoModLoader")).FullName,
            ClientModManifest.BootstrapFileName);
        File.WriteAllBytes(bootstrap, [1]);
        var client = new ClientInstallation(Path.Combine(_cache, "client with spaces", "Dofus.x64"));
        string managed = Directory.CreateDirectory(Path.Combine(client.Root, "MelonLoader", "net6")).FullName;
        File.Copy(typeof(HttpStatusCode).Assembly.Location, Path.Combine(managed, "MelonLoader.dll"));
        File.WriteAllText(Path.Combine(client.Root, ClientInstallation.LinuxMelonLoader), "");
        ClientModEntry mod = Entry("Heroes.dll", [2]);
        using var feed = new Feed(mod);
        feed.Blobs.Add(mod.Name, [2]);
        using var http = new HttpClient(feed);
        var plan = ClientModDeployment.Prepare(http, Server, bundle, _cache, "", client.HasMelonLoader,
            (_, _, _) => true);

        var start = LauncherService.ClientStartInfo(client.Executable, 1280, 720, 25, "hash", "fr", "",
            Path.Combine(_cache, "config"), Path.Combine(_cache, "logs"), preloadNativeMods: true);
        ClientModDeployment.Activate(client.Root, bundle, plan, start);

        Assert.StartsWith("./" + ClientInstallation.LinuxMelonLoader, start.Environment["LD_PRELOAD"]);
        Assert.Equal(plan.ManifestPath, start.Environment[ClientModManifest.LaunchManifestEnvironmentVariable]);
        Assert.Equal(new[] { mod }, Read(plan).Mods);
        Assert.True(BundledClientMods.SameContent(bootstrap, Path.Combine(client.Root, "Plugins", ClientModManifest.BootstrapFileName)));
        Assert.True(BundledClientMods.SameContent(shippedFix, Path.Combine(client.Root, "Mods", ClientModManifest.RequiredClientDll)));
    }

    [Fact]
    public void A_corrupted_dll_cannot_publish_a_launch_plan_and_can_be_retried()
    {
        byte[] correct = [1, 2, 3];
        ClientModEntry entry = Entry("Extra.dll", correct);
        using var feed = new Feed(entry);
        feed.Blobs.Add(entry.Name, [4, 5, 6]);
        using var http = new HttpClient(feed);
        Assert.Throws<InvalidDataException>(() => Prepare(http, "", (_, _, _) => true));
        Assert.Empty(Directory.GetFiles(_cache, "*", SearchOption.AllDirectories));

        feed.Blobs[entry.Name] = correct;
        Assert.True(File.Exists(Prepare(http, "", (_, _, _) => true).ManifestPath));
    }

    [Theory]
    [InlineData(true, Roles.Jugador, false)]
    [InlineData(true, Roles.Administrador, true)]
    [InlineData(false, Roles.Jugador, false)]
    [InlineData(false, Roles.Administrador, false)]
    public void Tokens_require_bundled_mod_contents_even_after_approval(bool bundledOnly, int role, bool controlToken)
    {
        const string name = "Bundled.dll";
        const string token = "session-token";
        string bundled = Directory.CreateDirectory(Path.Combine(_cache, "bundle", "mods")).FullName;
        File.WriteAllBytes(Path.Combine(bundled, name), [1]);
        byte[] serverDll = bundledOnly ? [1] : [2];
        using var feed = new Feed(Entry(name, serverDll));
        feed.Blobs.Add(name, serverDll);
        using var http = new HttpClient(feed);

        var plan = Prepare(http, "", (_, _, _) => !bundledOnly);
        Assert.Equal(bundledOnly, plan.NewApproval.Length == 0);
        var reused = Prepare(http, plan.NewApproval, (_, _, _) => throw new InvalidOperationException("Unexpected approval"));

        foreach (var launchPlan in new[] { plan, reused })
        {
            var start = new ProcessStartInfo();
            start.Environment.Clear();
            LauncherService.SetClientAccountEnvironment(start, launchPlan, role, token);

            Assert.Equal(bundledOnly, start.Environment.ContainsKey("JONDO_PLAYER_TOKEN"));
            Assert.Equal(controlToken, start.Environment.ContainsKey("JONDO_CONTROL_TOKEN"));
            if (bundledOnly) Assert.Equal(token, start.Environment["JONDO_PLAYER_TOKEN"]);
            if (controlToken) Assert.Equal(token, start.Environment["JONDO_CONTROL_TOKEN"]);
        }
    }

    [Theory]
    [InlineData("Cazacabezas.dll")]
    [InlineData("heroes.DLL")]
    public void An_empty_manifest_sets_aside_bundled_and_previously_installed_mods(string legacyName)
    {
        const string bundledName = "Bundled.dll";
        const string unrelatedName = "Custom.dll";
        const string retiredName = "Retired.dll";
        string bundled = Directory.CreateDirectory(Path.Combine(_cache, "bundle", "mods")).FullName;
        File.WriteAllBytes(Path.Combine(bundled, bundledName), [1]);
        string shippedFix = ReleaseFix();
        string client = Directory.CreateDirectory(Path.Combine(_cache, "client")).FullName;
        string mods = Directory.CreateDirectory(Path.Combine(client, "Mods")).FullName;
        File.WriteAllBytes(Path.Combine(mods, bundledName), [2]);
        File.WriteAllBytes(Path.Combine(mods, unrelatedName), [3]);
        File.WriteAllBytes(Path.Combine(mods, retiredName), [4]);
        File.WriteAllBytes(Path.Combine(mods, legacyName), [5]);
        BundledClientMods.Remember(client, retiredName);
        using var feed = new Feed();
        using var http = new HttpClient(feed);
        var plan = Prepare(http, "", (_, _, _) => true);

        ClientModDeployment.Activate(client, Path.Combine(_cache, "bundle"), plan, new ProcessStartInfo());

        Assert.True(plan.BundledOnly);
        Assert.False(File.Exists(Path.Combine(mods, bundledName)));
        Assert.Single(Directory.GetFiles(mods, bundledName + ".jondo-legacy-*"));
        Assert.False(File.Exists(Path.Combine(mods, retiredName)));
        Assert.Single(Directory.GetFiles(mods, retiredName + ".jondo-legacy-*"));
        Assert.False(File.Exists(Path.Combine(mods, legacyName)));
        Assert.Single(Directory.GetFiles(mods, legacyName + ".jondo-legacy-*"));
        Assert.True(File.Exists(Path.Combine(mods, unrelatedName)));
        Assert.True(BundledClientMods.SameContent(shippedFix, Path.Combine(mods, ClientModManifest.RequiredClientDll)));
    }

    [Fact]
    public void A_server_without_a_manifest_gets_the_jondofix_from_an_older_release_bundle()
    {
        string bundle = Path.Combine(_cache, "bundle");
        string legacySource = Directory.CreateDirectory(Path.Combine(bundle, "mods")).FullName;
        string shippedFix = Path.Combine(legacySource, ClientModManifest.RequiredClientDll);
        File.WriteAllBytes(shippedFix, [10, 11]);
        using var feed = new Feed { Status = HttpStatusCode.NotFound };
        using var http = new HttpClient(feed);
        var plan = Prepare(http, "", (_, _, _) => throw new InvalidOperationException("Unexpected approval"));
        string client = Path.Combine(_cache, "client");

        ClientModDeployment.Activate(client, bundle, plan, new ProcessStartInfo());

        Assert.False(plan.UsesManifest);
        Assert.True(plan.BundledOnly);
        Assert.Empty(feed.Downloads);
        Assert.Empty(BundledClientMods.Discover(bundle));
        Assert.True(BundledClientMods.SameContent(shippedFix, Path.Combine(client, "Mods", ClientModManifest.RequiredClientDll)));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NotFound)]
    public void MelonLoader_is_required_before_contacting_the_server(HttpStatusCode status)
    {
        using var feed = new Feed { Status = status };
        using var http = new HttpClient(feed);

        var error = Assert.Throws<InvalidOperationException>(() => ClientModDeployment.Prepare(http, Server,
            Path.Combine(_cache, "bundle"), _cache, "", hasMelonLoader: false, approve: null));

        Assert.Contains("MelonLoader", error.Message);
        Assert.Equal(0, feed.Requests);
    }

    [Fact]
    public void An_old_manifest_cannot_supply_jondofix_and_keeps_its_optional_mod_order()
    {
        string shippedFix = ReleaseFix();
        string bundle = Path.Combine(_cache, "bundle");
        string client = Path.Combine(_cache, "client");
        string mods = Directory.CreateDirectory(Path.Combine(client, "Mods")).FullName;
        string installed = Path.Combine(mods, ClientModManifest.RequiredClientDll);
        File.WriteAllBytes(installed, [2]);
        BundledClientMods.Remember(client, ClientModManifest.RequiredClientDll);
        ClientModEntry first = Entry("First.dll", [3]);
        ClientModEntry second = Entry("Second.dll", [4]);
        ClientModEntry redirect = Entry("jondofix.DLL", [5]);
        using var feed = new Feed(first, redirect, second);
        feed.Blobs.Add(first.Name, [3]);
        feed.Blobs.Add(second.Name, [4]);
        using var http = new HttpClient(feed);
        var plan = Prepare(http, "", (_, manifest, _) =>
        {
            Assert.Equal(new[] { first, second }, manifest.Mods);
            return true;
        });
        byte[] launchManifest = File.ReadAllBytes(plan.ManifestPath);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(launchManifest)), Path.GetFileNameWithoutExtension(plan.ManifestPath));
        Assert.Equal(new[] { first, second }, Read(plan).Mods);
        Assert.Equal(new[] { first.Name, second.Name }, feed.Downloads);

        feed.Manifest = new ClientModManifest([redirect]);
        var legacyFixOnly = Prepare(http, "", (_, _, _) => throw new InvalidOperationException("Unexpected approval"));
        ClientModDeployment.Activate(client, bundle, legacyFixOnly, new ProcessStartInfo());

        Assert.Empty(legacyFixOnly.ManifestPath);
        Assert.True(BundledClientMods.SameContent(shippedFix, installed));
        Assert.Empty(Directory.GetFiles(mods, "*.jondo-legacy-*"));
    }

    [Fact]
    public void A_missing_release_copy_blocks_launch()
    {
        using var feed = new Feed();
        using var http = new HttpClient(feed);
        var plan = Prepare(http, "", (_, _, _) => true);

        var error = Assert.Throws<FileNotFoundException>(() => ClientModDeployment.Activate(
            Path.Combine(_cache, "client"), Path.Combine(_cache, "bundle"), plan, new ProcessStartInfo()));

        Assert.Contains(ClientModManifest.RequiredClientDll, error.Message);
    }

    private string ReleaseFix()
    {
        string directory = Directory.CreateDirectory(Path.Combine(_cache, "bundle", "JondoFix")).FullName;
        string path = Path.Combine(directory, ClientModManifest.RequiredClientDll);
        File.WriteAllBytes(path, [10, 11]);
        return path;
    }

    private ClientModDeployment.Plan Prepare(HttpClient http, string trusted,
        Func<string, ClientModManifest, string, bool> approve)
        => ClientModDeployment.Prepare(http, Server, Path.Combine(_cache, "bundle"), _cache, trusted, hasMelonLoader: true, approve);

    private static ClientModManifest Read(ClientModDeployment.Plan plan)
        => JsonSerializer.Deserialize<ClientModManifest>(File.ReadAllBytes(plan.ManifestPath))!;

    private static ClientModEntry Entry(string file, byte[] bytes)
        => new(file, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));

    public void Dispose()
    {
        if (Directory.Exists(_cache)) Directory.Delete(_cache, recursive: true);
    }

    private sealed class Feed(params ClientModEntry[] mods) : HttpClientHandler
    {
        public ClientModManifest Manifest { get; set; } = new(mods);
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public int Requests { get; private set; }
        public Dictionary<string, byte[]> Blobs { get; } = new();
        public List<string> Downloads { get; } = new();

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal(HttpMethod.Get, request.Method);
            string path = request.RequestUri!.AbsolutePath;
            if (path == ClientModManifest.Route)
                return new HttpResponseMessage(Status)
                {
                    Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(Manifest))
                };
            string name = Uri.UnescapeDataString(path[(ClientModManifest.Route.Length + 1)..]);
            Downloads.Add(name);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Blobs[name]) };
        }
    }
}
