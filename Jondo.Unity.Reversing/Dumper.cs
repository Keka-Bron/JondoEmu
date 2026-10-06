using Cpp2IL.Core.OutputFormats;

namespace Jondo.Unity.Reversing;

/// <summary>
/// The protocol assembly, taken from a client that only has the binary and the metadata.
///
/// The client one installs already brings <c>cpp2il_out</c> because MelonLoader leaves it on starting. A
/// client downloaded from the CDN does not: it brings the raw binary. This does the same step
/// MelonLoader would, with the same library and the same output format.
///
/// ─── Why the .dll is written and the client is not read directly ────────────────────────
///
/// Cpp2IL already has the protocol in memory as soon as it opens the client, so taking the
/// messages from there looks like the obvious shortcut. It is not done, and on purpose.
///
/// Everything measured so far —the 68.3% ceiling, the 11.3% jump, the 293 anchors— came from
/// reading the .dll with MetadataLoadContext. A second path reading the same data from somewhere else
/// would give numbers that cannot be compared with those, and comparing is exactly what this
/// chain exists for. If the new number comes out different I want to know it is because of the patches, not because I
/// switched readers halfway through the experiment.
///
/// ─── All are written, even if only one matters ──────────────────────────────────────────
///
/// The first version only wrote <c>Ankama.Dofus.Protocol.Game.dll</c>, which is the only one
/// read. It gave zero messages. The reader opens the assembly with MetadataLoadContext, and to know whether
/// a class implements <c>IMessage</c> it has to be able to resolve <c>IMessage</c>, which lives in another
/// assembly; with the folder empty it resolves nothing and no class looks like a message.
///
/// So the whole dump is written, which is what MelonLoader leaves and what all the
/// measurements are made with. It is 69 MB per version —not the two hundred it seemed—, that is half a gig for
/// the chain of eight. Cheap so as not to have to trust two different paths.
/// </summary>
public static class Dumper
{
    /// <summary>Where MelonLoader leaves the dump, which is where <see cref="Mapper.ProtocolDll"/> looks for it.</summary>
    public static string Where(string clientFolder)
        => Path.Combine(clientFolder, "MelonLoader", "Dependencies", "Il2CppAssemblyGenerator",
                        "Cpp2IL", "cpp2il_out");

    /// <summary>
    /// Leaves the protocol assembly in its place and returns the path.
    ///
    /// If it is already there, it does nothing: opening the client and rebuilding the assemblies is half a minute per
    /// version, and the chain is walked more than once.
    /// </summary>
    public static string Protocol(string clientFolder, Action<string>? report = null)
    {
        const string wanted = "Ankama.Dofus.Protocol.Game";

        string folder = Where(clientFolder);
        string path = Path.Combine(folder, wanted + ".dll");
        if (File.Exists(path)) return path;

        report?.Invoke($"  {Path.GetFileName(clientFolder)}: abriendo el cliente…");
        using var client = new ClientReader(clientFolder);

        report?.Invoke($"  {Path.GetFileName(clientFolder)}: reconstruyendo los ensamblados…");
        var format = new AsmResolverDllOutputFormatDefault();
        var built = format.BuildAssemblies(client.App);

        Directory.CreateDirectory(folder);
        int written = 0, failed = 0;
        foreach (var assembly in built)
        {
            string name = assembly.Name?.ToString() ?? "";
            if (name.Length == 0) continue;
            try
            {
                assembly.ManifestModule!.Write(Path.Combine(folder, name + ".dll"));
                written++;
            }
            catch (Exception e)
            {
                // A game assembly that cannot be written does not ruin the version: the one that
                // matters is the protocol's, and that one is complained about. But they are counted, because if
                // many fail the dump is not comparable with MelonLoader's.
                failed++;
                if (name == wanted)
                    throw new InvalidOperationException($"{clientFolder}: no se ha podido escribir {wanted}", e);
            }
        }

        if (!File.Exists(path))
            throw new InvalidOperationException($"en {clientFolder} no se ha reconstruido {wanted}");

        report?.Invoke($"  {Path.GetFileName(clientFolder)}: {written:N0} ensamblados escritos" +
                       (failed > 0 ? $", {failed:N0} fallidos" : ""));
        return path;
    }
}
