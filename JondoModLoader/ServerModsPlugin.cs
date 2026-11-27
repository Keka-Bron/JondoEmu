using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Jondo.Unity.Launcher;
using MelonLoader;

[assembly: MelonInfo(typeof(JondoModLoader.ServerModsPlugin), "Jondo server mod loader", "1.0.0", "Jondo")]

namespace JondoModLoader;

public sealed class ServerModsPlugin : MelonPlugin
{
    public override void OnPreModsLoaded()
    {
        string? manifestPath = Environment.GetEnvironmentVariable(ClientModManifest.LaunchManifestEnvironmentVariable);
        if (string.IsNullOrEmpty(manifestPath)) return;

        try
        {
            LoadApprovedManifest(manifestPath);
        }
        catch (Exception error)
        {
            LoggerInstance.Error(error);
            // The launcher asked for this manifest. Loading only some of it would leave the client
            // running with an incomplete configuration.
            Environment.Exit(1);
        }
    }

    private void LoadApprovedManifest(string manifestPath)
    {
        string manifests = Path.GetDirectoryName(manifestPath) ?? throw new InvalidDataException("Invalid client mod manifest path.");
        string cache = Path.GetDirectoryName(manifests) ?? throw new InvalidDataException("Invalid client mods cache.");
        string fingerprint = Path.GetFileNameWithoutExtension(manifestPath);
        byte[] json = ReadVerified(manifestPath, fingerprint, ClientModManifest.MaxManifestBytes);
        ClientModManifest manifest = JsonSerializer.Deserialize<ClientModManifest>(json)
            ?? throw new InvalidDataException("No approved client mod manifest.");
        if (manifest.Mods is not { Length: <= ClientModManifest.MaxEntries })
            throw new InvalidDataException("Invalid client mod manifest list.");

        // MelonLoader sorts event callbacks by Priority, even for manually registered Melons.
        // Assign a distinct priority to each entry before registration so initialization follows
        // the same order as assembly loading and registration.
        PropertyInfo priority = typeof(MelonBase).GetProperty(nameof(MelonBase.Priority))
            ?? throw new InvalidOperationException("This version of MelonLoader does not support ordered registration.");
        var assemblies = new List<(string Name, MelonAssembly Assembly)>(manifest.Mods.Length);
        foreach (ClientModEntry entry in manifest.Mods)
        {
            if (!ClientModManifest.IsDllName(entry.Name) || !ClientModManifest.IsDllSize(entry.Size) ||
                entry.Sha256 is not { Length: 64 } || !entry.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid approved client mod entry.");
            string path = Path.Combine(cache, "blobs", entry.Sha256.ToLowerInvariant() + ".dll");
            byte[] bytes = ReadVerified(path, entry.Sha256, entry.Size, exactLength: true);
            var assembly = MelonAssembly.LoadRawMelonAssembly(path, bytes, loadMelons: false)
                ?? throw new InvalidDataException($"Could not load client mod {entry.Name}.");
            if (!string.Equals(assembly.Location, path, StringComparison.Ordinal))
                throw new InvalidDataException($"Client mod {entry.Name} conflicts with an assembly already loaded.");
            assemblies.Add((entry.Name, assembly));
        }

        for (int i = 0; i < assemblies.Count; i++)
        {
            (string name, MelonAssembly assembly) = assemblies[i];
            assembly.LoadMelons();
            if (assembly.RottenMelons.Count != 0)
                throw new InvalidDataException($"Could not initialize client mod {name}.");
            foreach (MelonBase melon in assembly.LoadedMelons)
            {
                if (melon is not MelonMod)
                    throw new InvalidDataException($"Client mod {name} must be a MelonMod.");
                priority.SetValue(melon, 1000 + i);
                if (!melon.Register())
                    throw new InvalidDataException($"Could not register client mod {name}.");
            }
        }
    }

    private static byte[] ReadVerified(string path, string fingerprint, long maximumLength, bool exactLength = false)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        long length = stream.Length;
        if (length > maximumLength || exactLength && length != maximumLength)
            throw new InvalidDataException($"Invalid size for {Path.GetFileName(path)}.");
        var bytes = new byte[(int)length];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) throw new EndOfStreamException(path);
            offset += read;
        }
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Hash verification failed for {Path.GetFileName(path)}.");
        return bytes;
    }
}
