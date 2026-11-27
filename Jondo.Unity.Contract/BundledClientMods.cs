using System.Security.Cryptography;

namespace Jondo.Unity.Launcher;

/// <summary>Discovers the standalone client DLLs shipped in an installation or checkout.</summary>
public static class BundledClientMods
{
    public const string RedirectDll = "JondoFix.dll";
    private const string ReceiptDirectory = ".jondo-managed";
    private const string ReceiptSuffix = ".receipt";

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
        }

        string checkoutRedirect = Path.Combine(root, "JondoFix", RedirectDll);
        if (File.Exists(checkoutRedirect)) files.TryAdd(RedirectDll, checkoutRedirect);
        return files;
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
        if (!ClientModManifest.IsDllName(name)) return;
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
            if (ClientModManifest.IsDllName(name)) yield return name;
        }
    }

    public static void SetAsideLegacyCopies(string clientRoot, IEnumerable<string> names)
    {
        string modsDirectory = Path.Combine(clientRoot, "Mods");
        if (!Directory.Exists(modsDirectory)) return;
        var managed = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
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
