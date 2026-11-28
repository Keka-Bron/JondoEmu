using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;

namespace Jondo.Unity.Launcher;

public static class ClientModDeployment
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false });

    public sealed record Plan(string ManifestPath, bool UsesManifest, string NewApproval, IReadOnlyList<string> ManagedNames,
        bool BundledOnly);

    public static Plan Prepare(Uri server, string bundleRoot, string cache, string trusted,
        bool hasMelonLoader, Func<string, ClientModManifest, string, bool>? approve)
        => Prepare(Http, server, bundleRoot, cache, trusted, hasMelonLoader, approve);

    public static Plan Prepare(HttpClient http, Uri server, string bundleRoot, string cache, string trusted,
        bool hasMelonLoader, Func<string, ClientModManifest, string, bool>? approve)
    {
        if (!hasMelonLoader)
            throw new InvalidOperationException("Install MelonLoader to launch the Jondo client.");

        var manifestUri = new Uri(server, ClientModManifest.Route);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUri);
        using var response = http.Send(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) // An older server: the release JondoFix only.
            return new Plan("", UsesManifest: false, "", [], BundledOnly: true);
        response.EnsureSuccessStatusCode();

        using var contents = new MemoryStream();
        CopyBounded(response.Content, contents, ClientModManifest.MaxManifestBytes, deadline.Token);
        byte[] json = contents.ToArray();
        ClientModManifest manifest = JsonSerializer.Deserialize<ClientModManifest>(json)
            ?? throw new InvalidDataException("The server returned an empty client mod manifest.");
        Validate(manifest);

        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(json));
        // Older manifests can still advertise JondoFix. Load only the release copy.
        if (manifest.Mods.Any(mod => ClientModManifest.IsRequiredClientDll(mod.Name)))
        {
            manifest = new ClientModManifest(manifest.Mods.Where(mod => !ClientModManifest.IsRequiredClientDll(mod.Name)).ToArray());
            json = JsonSerializer.SerializeToUtf8Bytes(manifest);
        }

        var bundledMods = BundledClientMods.Discover(bundleRoot);
        string? BundledPath(ClientModEntry mod) => bundledMods.GetValueOrDefault(mod.Name);
        bool bundledOnly = manifest.Mods.All(mod => Matches(BundledPath(mod), mod));
        bool approvalNeeded = (trusted.Length != 0 || !bundledOnly) &&
            !fingerprint.Equals(trusted, StringComparison.OrdinalIgnoreCase);
        if (approvalNeeded)
        {
            string host = server.Host;
            if (approve == null)
                throw new InvalidOperationException($"The mod manifest for {host} requires approval. SHA-256 {fingerprint}.");
            if (!approve(host, manifest, fingerprint)) throw new ApprovalDeclinedException();
        }

        string newApproval = approvalNeeded ? fingerprint : "";
        var managedNames = bundledMods.Keys.Concat(manifest.Mods.Select(mod => mod.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (manifest.Mods.Length == 0)
            return new Plan("", UsesManifest: true, newApproval, managedNames, bundledOnly);
        string blobs = Path.Combine(cache, "blobs");
        Directory.CreateDirectory(blobs);
        foreach (ClientModEntry mod in manifest.Mods)
        {
            string target = Path.Combine(blobs, mod.Sha256.ToLowerInvariant() + ".dll");
            if (Matches(target, mod)) continue;
            string staged = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                string? bundled = BundledPath(mod);
                if (Matches(bundled, mod))
                    File.Copy(bundled!, staged);
                else
                    Download(http, new Uri(manifestUri.AbsoluteUri + "/" + Uri.EscapeDataString(mod.Name)), mod, staged);
                if (!Matches(staged, mod)) throw new InvalidDataException($"The DLL for {mod.Name} does not match the approved manifest.");
                File.Move(staged, target, overwrite: true);
            }
            finally
            {
                File.Delete(staged);
            }
        }

        string manifestDirectory = Path.Combine(cache, "manifests");
        Directory.CreateDirectory(manifestDirectory);
        string launchFingerprint = Convert.ToHexStringLower(SHA256.HashData(json));
        string path = Path.Combine(manifestDirectory, launchFingerprint + ".json");
        var plan = new Plan(path, UsesManifest: true, newApproval, managedNames, bundledOnly);
        if (Matches(path, json.Length, launchFingerprint)) return plan;
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(pending, json);
            File.Move(pending, path, overwrite: true);
        }
        finally
        {
            File.Delete(pending);
        }
        return plan;
    }

    public sealed class ApprovalDeclinedException : Exception { }

    private static void Validate(ClientModManifest manifest)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (manifest.Mods is not { Length: <= ClientModManifest.MaxEntries })
            throw new InvalidDataException("The server returned an invalid client mod list.");
        foreach (ClientModEntry mod in manifest.Mods)
        {
            if (mod == null)
                throw new InvalidDataException("The server returned an invalid client mod entry.");
            if (ClientModManifest.IsRequiredClientDll(mod.Name)) continue;
            if (!ClientModManifest.IsDllName(mod.Name) ||
                !names.Add(mod.Name) || !ClientModManifest.IsDllSize(mod.Size) || mod.Sha256 is not { Length: 64 } ||
                !mod.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("The server returned an invalid client mod entry.");
        }
    }

    public static void Activate(string clientRoot, string bundleRoot, Plan plan, ProcessStartInfo start,
        Func<string, string>? translatePath = null)
    {
        start.Environment.Remove(ClientModManifest.LaunchManifestEnvironmentVariable);
        BundledClientMods.InstallRequiredClientDll(bundleRoot, clientRoot);
        if (!plan.UsesManifest) return;
        if (plan.ManifestPath.Length > 0)
        {
            string melonLoader = Path.Combine(clientRoot, "MelonLoader", "net6", "MelonLoader.dll");
            if (!File.Exists(melonLoader) ||
                System.Reflection.AssemblyName.GetAssemblyName(melonLoader).Version < new Version(0, 7, 3, 0))
                throw new InvalidOperationException("Server mod loading requires MelonLoader 0.7.3 or newer.");
            string bootstrap = Path.Combine(bundleRoot, "JondoModLoader", ClientModManifest.BootstrapFileName);
            if (!File.Exists(bootstrap)) throw new FileNotFoundException("The Jondo mod loader is missing from this installation.", bootstrap);
            string plugins = Path.Combine(clientRoot, "Plugins");
            Directory.CreateDirectory(plugins);
            string installed = Path.Combine(plugins, ClientModManifest.BootstrapFileName);
            if (!File.Exists(installed) || !BundledClientMods.SameContent(installed, bootstrap))
                BundledClientMods.InstallDll(bootstrap, installed);
            start.Environment[ClientModManifest.LaunchManifestEnvironmentVariable] =
                translatePath?.Invoke(plan.ManifestPath) ?? plan.ManifestPath;
        }

        // Older launchers and first-run setup placed these in MelonLoader's default scan folder.
        BundledClientMods.SetAsideLegacyCopies(clientRoot, plan.ManagedNames.Concat(BundledClientMods.ManagedNames(clientRoot)));
    }

    private static bool Matches(string? path, ClientModEntry mod) => Matches(path, mod.Size, mod.Sha256);

    private static bool Matches(string? path, long size, string sha256)
    {
        if (path == null || !File.Exists(path)) return false;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return file.Length == size &&
            Convert.ToHexStringLower(SHA256.HashData(file)).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void Download(HttpClient http, Uri uri, ClientModEntry mod, string target)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = http.Send(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        using var file = File.Create(target);
        CopyBounded(response.Content, file, mod.Size, deadline.Token);
    }

    private static void CopyBounded(HttpContent content, Stream target, long limit, CancellationToken cancel)
    {
        if (content.Headers.ContentLength > limit) throw new InvalidDataException("The server response exceeds its allowed size.");
        using var source = content.ReadAsStream(cancel);
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = source.ReadAsync(buffer.AsMemory(), cancel).AsTask().GetAwaiter().GetResult()) != 0)
        {
            if (read > limit - target.Length) throw new InvalidDataException("The server response exceeds its allowed size.");
            target.Write(buffer, 0, read);
        }
    }
}
