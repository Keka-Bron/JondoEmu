using AssetRipper.Primitives;
using Cpp2IL.Core;
using Cpp2IL.Core.Model.Contexts;

namespace Jondo.Unity.Reversing;

/// <summary>
/// El cliente entero abierto por dentro: no las fachadas, el código de verdad.
///
/// Hasta aquí el protocolo se sacaba de los ensamblados que deja Cpp2IL en <c>cpp2il_out</c>, y eso
/// basta para los NÚMEROS y los TIPOS de cada mensaje. Pero esos ensamblados están huecos: sus
/// métodos no tienen cuerpo. Medido: de los 110.811 métodos de <c>Core.dll</c>, ninguno pasa de
/// dieciséis bytes de IL, y la mediana son dos. El código del juego no está ahí; está compilado a
/// máquina dentro de <c>GameAssembly.dll</c>.
///
/// Por eso esto no lee ficheros .dll sino el cliente en crudo —el binario más los metadatos— con la
/// misma biblioteca que usa Cpp2IL. A cambio de cargar 110 MB se obtiene lo que no había: qué hace
/// cada método. La conversión a ISIL —un lenguaje intermedio independiente de la máquina— resuelve
/// además las llamadas y los usos de metadatos, así que un <c>call</c> deja de ser una dirección y
/// pasa a ser «llama a tal método de tal clase».
///
/// Coste medido en este cliente: nueve segundos en cargar y veinte en analizar los 366.413 métodos.
/// </summary>
public sealed class ClientReader : IDisposable
{
    /// <summary>Prepara el cliente. La ruta es la carpeta donde está Dofus.exe.</summary>
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

        // Si esto no está, lo que se ha abierto no es el cliente de Dofus. Mejor decirlo aquí que
        // dejar que reviente veinte segundos después, en medio del barrido y sin explicar por qué.
        Protocol = App.GetAssemblyByName("Ankama.Dofus.Protocol.Game")
                   ?? throw new InvalidOperationException(
                       $"en {clientFolder} no hay Ankama.Dofus.Protocol.Game: ¿es la carpeta del cliente?");
    }

    public string Folder { get; }
    public UnityVersion Version { get; }
    public ApplicationAnalysisContext App { get; }

    /// <summary>El ensamblado del protocolo del juego, donde viven los mensajes.</summary>
    public AssemblyAnalysisContext Protocol { get; }

    /// <summary>
    /// Los mensajes del protocolo: los tipos que implementan <c>IMessage</c>.
    ///
    /// Se pregunta por la interfaz y no por el nombre de tres letras a propósito. Los dos criterios
    /// dan lo mismo hoy —2.169 tipos en 3.6.10.10— pero el nombre es una costumbre de Ankama y la
    /// interfaz es lo que de verdad hace que algo viaje por el cable.
    /// </summary>
    public IEnumerable<TypeAnalysisContext> Messages()
        => Protocol.Types.Where(t => t.InterfaceContexts.Any(i => i.Name is "IMessage" or "IBufferMessage"));

    /// <summary>
    /// Todos los métodos del cliente, con el ensamblado al que pertenecen.
    ///
    /// Incluye los de UnityEngine y los de mscorlib, que no interesan por sí mismos pero sí como
    /// eslabones: un método del juego puede llegar a un mensaje pasando por una lista genérica.
    /// </summary>
    public IEnumerable<MethodAnalysisContext> AllMethods()
        => App.Assemblies.SelectMany(a => a.Types).SelectMany(t => t.Methods);

    public void Dispose() => Cpp2IlApi.ResetInternalState();

    /// <summary>
    /// El arranque de Cpp2IL, que se hace UNA vez por proceso.
    ///
    /// <c>Init</c> registra los complementos recorriendo una lista mientras le añade cosas, así que
    /// la segunda llamada revienta con «Collection was modified». No se nota abriendo un cliente,
    /// que es lo que se hacía hasta ahora; se nota abriendo ocho seguidos para recorrer la cadena.
    /// <c>ResetInternalState</c>, que es lo que deja Dispose, limpia el cliente cargado pero no los
    /// complementos, así que basta con no repetir el registro.
    /// </summary>
    private static bool _ready;

    private static void Prepare()
    {
        if (_ready) return;
        Cpp2IlApi.Init();
        _ready = true;
    }
}
