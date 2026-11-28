using System.Security.Cryptography;

namespace Jondo.Unity.Launcher;

/// <summary>Discovers the standalone client DLLs shipped in an installation or checkout.</summary>
public static class BundledClientMods
{
    private const string ReceiptDirectory = ".jondo-managed";
    private const string ReceiptSuffix = ".receipt";

    // Launchers before manifest support installed these directly without receipts.
    private static readonly string[] LegacyDllNames = ["Cazacabezas.dll", "Heroes.dll"];

    public static IReadOnlyDictionary<string, string> Discover(string root)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string directory = Path.Combine(root, "mods");
        if (Directory.Exists(directory))
        {
            foreach (string path in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
            {
                string name = Path.GetFileName(path);
                if (ClientModManifest.IsDllName(name)) files.TryAdd(name, path);
            }

            // Release and Docker bundles ship the first-party mods as server packages,
            // mods/<id>/manifest.json naming the client DLL beside it.
            foreach (string package in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
            {
                string? asset = PackageClientAsset(Path.Combine(package, "manifest.json"));
                if (asset == null || !ClientModManifest.IsDllName(asset)) continue;
                string path = Path.Combine(package, asset);
                if (File.Exists(path)) files.TryAdd(asset, path);
            }
        }

        return files;
    }

    private static string? PackageClientAsset(string manifest)
    {
        if (!File.Exists(manifest)) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(manifest));
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("clientAsset", out var asset) &&
                   asset.ValueKind == System.Text.Json.JsonValueKind.String
                ? asset.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public static bool InstallRequiredClientDll(string root, string clientRoot)
    {
        const string name = ClientModManifest.RequiredClientDll;
        string shipped = Path.Combine(root, "JondoFix", name);
        if (!File.Exists(shipped)) shipped = Path.Combine(root, "mods", name); // Older release bundles.
        if (!File.Exists(shipped))
            throw new FileNotFoundException($"The required {name} is missing from this installation. Repair the launcher installation.", shipped);

        string mods = Path.Combine(clientRoot, "Mods");
        string installed = Path.Combine(mods, name);
        bool changed = !File.Exists(installed) || !SameContent(installed, shipped);
        if (changed)
        {
            Directory.CreateDirectory(mods);
            InstallDll(shipped, installed);
        }
        Remember(clientRoot, name);
        return changed;
    }

    internal static void InstallDll(string source, string installed)
    {
        string staged = installed + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, staged);
            try
            {
                File.Move(staged, installed, overwrite: true);
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && File.Exists(installed))
            {
                string aside = installed + ".old";
                File.Delete(aside);
                File.Move(installed, aside);
                File.Move(staged, installed, overwrite: true);
            }
        }
        finally
        {
            File.Delete(staged);
        }
    }

    public sealed record Update(string Name, string? Error);

    public static IReadOnlyList<Update> Install(string root, string clientRoot)
    {
        var updates = new List<Update>();
        string mods = Path.Combine(clientRoot, "Mods");
        foreach ((string name, string shipped) in Discover(root))
        {
            string installed = Path.Combine(mods, name);
            string aside = installed + ".old";
            try
            {
                // A prior launch may still have the old file mapped.
                try { File.Delete(aside); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                if (File.Exists(installed) && SameContent(installed, shipped))
                {
                    Remember(clientRoot, name);
                    continue;
                }

                Directory.CreateDirectory(mods);
                try
                {
                    File.Copy(shipped, installed, overwrite: true);
                }
                catch (IOException) when (File.Exists(installed) && !File.Exists(aside))
                {
                    File.Move(installed, aside);
                    File.Copy(shipped, installed, overwrite: true);
                }
                Remember(clientRoot, name);
                updates.Add(new Update(name, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                updates.Add(new Update(name, ex.Message));
            }
        }
        return updates;
    }

    public static void Remember(string clientRoot, string name)
    {
        if (!ClientModManifest.IsDllName(name) && !ClientModManifest.IsRequiredClientDll(name)) return;
        string receipts = Path.Combine(clientRoot, "Mods", ReceiptDirectory);
        Directory.CreateDirectory(receipts);
        using var receipt = new FileStream(Path.Combine(receipts, name + ReceiptSuffix), FileMode.OpenOrCreate,
            FileAccess.Write, FileShare.ReadWrite);
    }

    public static IEnumerable<string> ManagedNames(string clientRoot)
    {
        string receipts = Path.Combine(clientRoot, "Mods", ReceiptDirectory);
        if (!Directory.Exists(receipts)) yield break;
        foreach (string receipt in Directory.EnumerateFiles(receipts, "*" + ReceiptSuffix))
        {
            string name = Path.GetFileName(receipt)[..^ReceiptSuffix.Length];
            if (ClientModManifest.IsDllName(name) || ClientModManifest.IsRequiredClientDll(name)) yield return name;
        }
    }

    public static void SetAsideLegacyCopies(string clientRoot, IEnumerable<string> names)
    {
        string modsDirectory = Path.Combine(clientRoot, "Mods");
        if (!Directory.Exists(modsDirectory)) return;
        var managed = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        managed.UnionWith(LegacyDllNames);
        managed.Remove(ClientModManifest.RequiredClientDll);
        foreach (string legacy in Directory.EnumerateFiles(modsDirectory))
        {
            if (!managed.Contains(Path.GetFileName(legacy))) continue;
            try { File.Move(legacy, legacy + ".jondo-legacy-" + Guid.NewGuid().ToString("N")); }
            catch (FileNotFoundException) { } // Another launch already moved this copy.
        }
    }

    public static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var streamA = File.OpenRead(a);
        using var streamB = File.OpenRead(b);
        return SHA256.HashData(streamA).AsSpan().SequenceEqual(SHA256.HashData(streamB));
    }
}
