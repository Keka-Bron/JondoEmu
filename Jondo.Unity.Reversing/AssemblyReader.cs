using System.Reflection;

namespace Jondo.Unity.Reversing;

/// <summary>
/// Los ensamblados del cliente, leídos sin ejecutar nada.
///
/// MelonLoader deja en Il2CppAssemblies unos ensamblados de C# que reflejan lo que hay dentro del
/// cliente: las clases de los mensajes, con su nombre de tres letras y sus propiedades. Son
/// fachadas —por dentro llaman al código nativo— pero sus METADATOS son de verdad, y eso es lo
/// que se lee aquí.
///
/// Se abren con MetadataLoadContext y no con Assembly.Load a propósito: cargarlos de verdad
/// significaría ejecutar sus inicializadores, que buscan un runtime de Il2Cpp que aquí no existe.
/// Así se leen como lo que son: ficheros.
/// </summary>
public sealed class AssemblyReader : IDisposable
{
    private readonly MetadataLoadContext _context;

    public AssemblyReader(string assemblyPath)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!;

        // El resolvedor necesita ver TODO lo que el ensamblado referencia —el resto de los del
        // cliente y las bibliotecas de .NET— o al preguntar por un tipo devuelve una excepción en
        // vez del tipo.
        var paths = new List<string>(Directory.GetFiles(folder, "*.dll"));
        string runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        paths.AddRange(Directory.GetFiles(runtime, "*.dll"));

        // Las fachadas de Il2Cpp heredan de tipos que viven en el propio MelonLoader —una carpeta
        // más allá, en net6—, así que sin ella el tipo se encuentra pero no se puede ni preguntar
        // de qué hereda.
        string? padre = Path.GetDirectoryName(folder);
        if (padre != null)
        {
            string net6 = Path.Combine(padre, "net6");
            if (Directory.Exists(net6)) paths.AddRange(Directory.GetFiles(net6, "*.dll"));
        }

        // Sin quitar los repetidos no arranca: el volcado de Cpp2IL trae su propio mscorlib —crea
        // uno de mentira para todo lo que el juego usa del runtime— y el cargador se planta en
        // cuanto ve dos ensamblados con el mismo nombre. Gana el que esté al lado del que se abre,
        // que es el que de verdad describe a este cliente.
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

    /// <summary>Los tipos que hay dentro, sin que un tipo roto se lleve por delante a los demás.</summary>
    public IEnumerable<Type> Types()
    {
        Type?[] types;
        try { types = Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types; }
        foreach (var type in types) if (type != null) yield return type;
    }

    /// <summary>
    /// Los mensajes del protocolo: los que se llaman con tres letras minúsculas.
    ///
    /// Es lo que viaja por el cable —type.ankama.com/jsd— y lo que Ankama rota en cada parche.
    /// El resto de clases del ensamblado son ayudas, fábricas y enumerados.
    /// </summary>
    public IEnumerable<Type> ProtocolMessages()
        => Types().Where(t => t.Name.Length == 3 && t.Name.All(char.IsLower));

    public void Dispose() => _context.Dispose();
}
