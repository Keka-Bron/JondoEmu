using System.Security.Cryptography;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Network;

internal sealed class ClientModCatalog
{
    internal sealed record Asset(string Directory, string RelativePath)
    {
        public string Name => Path.GetFileName(RelativePath);
    }

    private readonly List<Asset> _ordered = [];
    private readonly Dictionary<string, Asset> _downloads = new(StringComparer.Ordinal);
    private readonly string? _orderError;

    public ClientModCatalog(string directory, IReadOnlyList<string> names, IReadOnlyList<Asset>? packageAssets = null)
    {
        var packages = new Dictionary<string, Asset>(StringComparer.OrdinalIgnoreCase);
        foreach (Asset asset in packageAssets ?? [])
            packages.TryAdd(asset.Name, asset);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            if (!ClientModManifest.IsDllName(name) || !seen.Add(name))
            {
                _orderError ??= $"Invalid or repeated client mod filename: {name}";
                continue;
            }

            Add(packages.Remove(name, out var replacement) ? replacement : new Asset(directory, name));
        }

        foreach (Asset asset in packageAssets ?? [])
        {
            if (packages.Remove(asset.Name, out var remaining)) Add(remaining);
        }
    }

    private void Add(Asset asset)
    {
        _ordered.Add(asset);
        _downloads.Add(asset.Name, asset);
    }

    public static string[] ListAvailable(string directory)
    {
        try
        {
            if (!Directory.Exists(directory) || IsLink(directory)) return [];

            return Directory.EnumerateFiles(directory, "*", new EnumerationOptions
                {
                    AttributesToSkip = FileAttributes.ReparsePoint,
                })
                .Select(Path.GetFileName)
                .Where(ClientModManifest.IsDllName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[Control] Could not discover client mods: {ex.Message}");
            return [];
        }
    }

    public ClientModManifest CreateManifest()
    {
        if (_orderError != null) throw new InvalidDataException(_orderError);

        var entries = new List<ClientModEntry>(Math.Min(_ordered.Count, ClientModManifest.MaxEntries));
        foreach (Asset asset in _ordered)
        {
            using FileStream? file = OpenFile(asset);
            if (file == null) continue;

            if (entries.Count == ClientModManifest.MaxEntries)
                throw new InvalidDataException($"Client mod order exceeds {ClientModManifest.MaxEntries} available DLLs.");

            entries.Add(new ClientModEntry(asset.Name, file.Length, Convert.ToHexStringLower(SHA256.HashData(file))));
        }

        return new ClientModManifest(entries.ToArray());
    }

    public FileStream? OpenDownload(string? name)
        => name != null && _downloads.TryGetValue(name, out Asset? asset) ? OpenFile(asset) : null;

    private static FileStream? OpenFile(Asset asset)
    {
        string path = Path.Combine(asset.Directory, asset.RelativePath);
        try
        {
            const FileAttributes DirectoryOrLink = FileAttributes.Directory | FileAttributes.ReparsePoint;
            string directory = asset.Directory;
            if ((File.GetAttributes(directory) & DirectoryOrLink) != FileAttributes.Directory) return null;
            string parents = Path.GetDirectoryName(asset.RelativePath) ?? "";
            foreach (string segment in parents.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                directory = Path.Combine(directory, segment);
                if ((File.GetAttributes(directory) & DirectoryOrLink) != FileAttributes.Directory) return null;
            }
            if ((File.GetAttributes(path) & DirectoryOrLink) != 0) return null;

            if (!ClientModManifest.IsDllSize(new FileInfo(path).Length)) throw InvalidSize(asset.Name);

            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (ClientModManifest.IsDllSize(file.Length)) return file;

            file.Dispose();
            throw InvalidSize(asset.Name);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static InvalidDataException InvalidSize(string name)
        => new($"Client mod {name} must be between 1 and {ClientModManifest.MaxDllBytes} bytes.");

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
