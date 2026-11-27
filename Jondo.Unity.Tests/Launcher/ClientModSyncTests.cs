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

    [Fact]
    public void An_empty_manifest_sets_aside_bundled_and_previously_installed_mods()
    {
        const string bundledName = "Bundled.dll";
        const string unrelatedName = "Custom.dll";
        const string retiredName = "Retired.dll";
        string bundled = Directory.CreateDirectory(Path.Combine(_cache, "bundle", "mods")).FullName;
        File.WriteAllBytes(Path.Combine(bundled, bundledName), [1]);
        string client = Directory.CreateDirectory(Path.Combine(_cache, "client")).FullName;
        string mods = Directory.CreateDirectory(Path.Combine(client, "Mods")).FullName;
        File.WriteAllBytes(Path.Combine(mods, bundledName), [2]);
        File.WriteAllBytes(Path.Combine(mods, unrelatedName), [3]);
        File.WriteAllBytes(Path.Combine(mods, retiredName), [4]);
        BundledClientMods.Remember(client, retiredName);
        using var feed = new Feed();
        using var http = new HttpClient(feed);
        var plan = Prepare(http, "", (_, _, _) => true);

        ClientModDeployment.Activate(client, Path.Combine(_cache, "bundle"), plan, new ProcessStartInfo());

        Assert.False(File.Exists(Path.Combine(mods, bundledName)));
        Assert.Single(Directory.GetFiles(mods, bundledName + ".jondo-legacy-*"));
        Assert.False(File.Exists(Path.Combine(mods, retiredName)));
        Assert.Single(Directory.GetFiles(mods, retiredName + ".jondo-legacy-*"));
        Assert.True(File.Exists(Path.Combine(mods, unrelatedName)));
    }

    private ClientModDeployment.Plan Prepare(HttpClient http, string trusted,
        Func<string, ClientModManifest, string, bool> approve)
        => ClientModDeployment.Prepare(http, Server, Path.Combine(_cache, "bundle"), _cache, trusted, hasMelonLoader: true, requiresRedirectMod: false, approve);

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
        public Dictionary<string, byte[]> Blobs { get; } = new();
        public List<string> Downloads { get; } = new();

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            string path = request.RequestUri!.AbsolutePath;
            if (path == ClientModManifest.Route)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(Manifest))
                };
            string name = Uri.UnescapeDataString(path[(ClientModManifest.Route.Length + 1)..]);
            Downloads.Add(name);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Blobs[name]) };
        }
    }
}
