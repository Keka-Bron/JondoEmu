using AssetRipper.Primitives;
using Cpp2IL.Core;
using Cpp2IL.Core.Model.Contexts;

namespace Jondo.Unity.Reversing;

/// <summary>
/// The whole client opened from the inside: not the façades, the real code.
///
/// Until now the protocol was taken from the assemblies Cpp2IL leaves in <c>cpp2il_out</c>, and that
/// is enough for each message's NUMBERS and TYPES. But those assemblies are hollow: their
/// methods have no body. Measured: of the 110,811 methods of <c>Core.dll</c>, none goes beyond
/// sixteen bytes of IL, and the median is two. The game's code is not there; it is compiled to
/// machine code inside <c>GameAssembly.dll</c>.
///
/// That is why this does not read .dll files but the raw client —the binary plus the metadata— with the
/// same library Cpp2IL uses. In exchange for loading 110 MB one gets what was missing: what
/// each method does. The conversion to ISIL —a machine-independent intermediate language— also resolves
/// the calls and the metadata uses, so a <c>call</c> stops being an address and
/// becomes «calls such method of such class».
///
/// Cost measured on this client: nine seconds to load and twenty to analyse the 366,413 methods.
/// </summary>
public sealed class ClientReader : IDisposable
{
    /// <summary>Prepares the client. The path is the folder where Dofus.exe is.</summary>
    public ClientReader(string clientFolder)
    {
        Folder = clientFolder;

        string binary = Path.Combine(clientFolder, "GameAssembly.dll");
        string metadata = Path.Combine(clientFolder, "Dofus_Data", "il2cpp_data", "Metadata",
                                       "global-metadata.dat");
        string player = Path.Combine(clientFolder, "UnityPlayer.dll");

        foreach (string needed in new[] { binary, metadata, player })
        {
            if (!File.Exists(needed)) throw new FileNotFoundException($"falta {needed}");
        }

        Prepare();
        Version = Cpp2IlApi.DetermineUnityVersion(player, clientFolder);
        Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, Version, false);

        App = Cpp2IlApi.CurrentAppContext;

        // If this is not there, what has been opened is not the Dofus client. Better to say so here than
        // to let it blow up twenty seconds later, in the middle of the sweep and without explaining why.
        Protocol = App.GetAssemblyByName("Ankama.Dofus.Protocol.Game")
                   ?? throw new InvalidOperationException(
                       $"en {clientFolder} no hay Ankama.Dofus.Protocol.Game: ¿es la carpeta del cliente?");
    }

    public string Folder { get; }
    public UnityVersion Version { get; }
    public ApplicationAnalysisContext App { get; }

    /// <summary>The game's protocol assembly, where the messages live.</summary>
    public AssemblyAnalysisContext Protocol { get; }

    /// <summary>
    /// The protocol's messages: the types implementing <c>IMessage</c>.
    ///
    /// The interface is asked for and not the three-letter name on purpose. Both criteria
    /// give the same today —2,169 types in 3.6.10.10— but the name is an Ankama habit and the
    /// interface is what really makes something travel on the wire.
    /// </summary>
    public IEnumerable<TypeAnalysisContext> Messages()
        => Protocol.Types.Where(t => t.InterfaceContexts.Any(i => i.Name is "IMessage" or "IBufferMessage"));

    /// <summary>
    /// All the client's methods, with the assembly they belong to.
    ///
    /// It includes those of UnityEngine and mscorlib, which are of no interest in themselves but are as
    /// links: a game method can reach a message going through a generic list.
    /// </summary>
    public IEnumerable<MethodAnalysisContext> AllMethods()
        => App.Assemblies.SelectMany(a => a.Types).SelectMany(t => t.Methods);

    public void Dispose() => Cpp2IlApi.ResetInternalState();

    /// <summary>
    /// Cpp2IL's startup, which is done ONCE per process.
    ///
    /// <c>Init</c> registers the plugins walking a list while adding things to it, so
    /// the second call blows up with «Collection was modified». It is not noticed opening one client,
    /// which is what was done until now; it is noticed opening eight in a row to walk the chain.
    /// <c>ResetInternalState</c>, which is what Dispose leaves, cleans the loaded client but not the
    /// plugins, so it is enough not to repeat the registration.
    /// </summary>
    private static bool _ready;

    private static void Prepare()
    {
        if (_ready) return;
        Cpp2IlApi.Init();
        _ready = true;
    }
}
