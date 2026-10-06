using System.Reflection;

namespace Jondo.Unity.Reversing;

/// <summary>
/// The client's assemblies, read without running anything.
///
/// MelonLoader leaves in Il2CppAssemblies some C# assemblies that mirror what is inside the
/// client: the message classes, with their three-letter name and their properties. They are
/// façades —inside they call the native code— but their METADATA is real, and that is what
/// is read here.
///
/// They are opened with MetadataLoadContext and not with Assembly.Load on purpose: really loading them
/// would mean running their initialisers, which look for an Il2Cpp runtime that does not exist here.
/// This way they are read as what they are: files.
/// </summary>
public sealed class AssemblyReader : IDisposable
{
    private readonly MetadataLoadContext _context;

    public AssemblyReader(string assemblyPath)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!;

        // The resolver needs to see EVERYTHING the assembly references —the rest of the
        // client's and the .NET libraries— or when asked for a type it returns an exception instead
        // of the type.
        var paths = new List<string>(Directory.GetFiles(folder, "*.dll"));
        string runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        paths.AddRange(Directory.GetFiles(runtime, "*.dll"));

        // The Il2Cpp façades inherit from types that live in MelonLoader itself —one folder
        // further, in net6—, so without it the type is found but one cannot even ask
        // what it inherits from.
        string? padre = Path.GetDirectoryName(folder);
        if (padre != null)
        {
            string net6 = Path.Combine(padre, "net6");
            if (Directory.Exists(net6)) paths.AddRange(Directory.GetFiles(net6, "*.dll"));
        }

        // Without removing the duplicates it does not start: the Cpp2IL dump brings its own mscorlib —it creates
        // a fake one for everything the game uses from the runtime— and the loader stops dead as soon
        // as it sees two assemblies with the same name. The one next to the one being opened wins,
        // which is the one that really describes this client.
        var unicos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string ruta in paths)
        {
            string nombre = Path.GetFileNameWithoutExtension(ruta);
            if (!unicos.ContainsKey(nombre)) unicos[nombre] = ruta;
        }

        _context = new MetadataLoadContext(new PathAssemblyResolver(unicos.Values.ToList()));
        Assembly = _context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
    }

    public Assembly Assembly { get; }

    /// <summary>The types inside, without a broken type taking the others down with it.</summary>
    public IEnumerable<Type> Types()
    {
        Type?[] types;
        try { types = Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types; }
        foreach (var type in types) if (type != null) yield return type;
    }

    /// <summary>
    /// The protocol's messages: the ones named with three lowercase letters.
    ///
    /// It is what travels on the wire —type.ankama.com/jsd— and what Ankama rotates on every patch.
    /// The rest of the assembly's classes are helpers, factories and enums.
    /// </summary>
    public IEnumerable<Type> ProtocolMessages()
        => Types().Where(t => t.Name.Length == 3 && t.Name.All(char.IsLower));

    public void Dispose() => _context.Dispose();
}
