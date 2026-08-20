using System.Reflection;
using LibCpp2IL;

namespace Jondo.Unity.Reversing;

/// <summary>
/// La cabecera de global-metadata.dat, leída del propio LibCpp2IL.
///
/// El bloque de 1,7 MB con los nombres reales del protocolo está en el fichero pero no lo referencia
/// nadie: ni el código lo carga como literal, ni es la tabla de tipos viva. La pregunta que queda es
/// si es un RESTO de la tabla anterior a la ofuscación y, sobre todo, si conserva el ORDEN. Si lo
/// conserva, la pareja sale por posición y se acabó el problema.
///
/// Para responder hay que saber qué región del fichero es cada cosa, y eso lo dice la cabecera.
/// No se parsea a mano: LibCpp2IL ya la tiene leída, así que se le pregunta. Los campos se sacan por
/// reflexión a propósito —cambian de nombre y de orden entre versiones de metadatos— y así la sonda
/// vale igual dentro de tres parches.
/// </summary>
public static class Header
{
    /// <summary>Una región del fichero: dónde empieza y cuánto ocupa.</summary>
    public sealed record Region(string Name, long Offset, long Size)
    {
        public bool Holds(long position) => position >= Offset && position < Offset + Size;
    }

    /// <summary>Todo lo que la cabecera dice, en crudo.</summary>
    public static Dictionary<string, long> Fields()
    {
        var metadata = LibCpp2IlMain.TheMetadata
                       ?? throw new InvalidOperationException("no hay metadatos cargados");

        object header = metadata.GetType()
            .GetField("metadataHeader", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(metadata)
            ?? throw new InvalidOperationException("no encuentro metadataHeader");

        return Numbers(header);
    }

    /// <summary>
    /// Los números que lleva un objeto dentro, sean campos o propiedades.
    ///
    /// Se miran las dos cosas porque adivinar cuál es sale caro: la primera versión sólo leía
    /// campos públicos y no encontró ni uno, y el resultado —«0 regiones declaradas»— parecía un
    /// hallazgo cuando era la sonda mirando donde no era.
    /// </summary>
    public static Dictionary<string, long> Numbers(object thing)
    {
        const BindingFlags Todos = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var values = new Dictionary<string, long>(StringComparer.Ordinal);

        void Note(string name, object? value)
        {
            switch (value)
            {
                case int number: values[name] = number; break;
                case uint unsigned: values[name] = unsigned; break;
                case long big: values[name] = big; break;
                case ulong huge: values[name] = (long)huge; break;
            }
        }

        foreach (var field in thing.GetType().GetFields(Todos))
            Note(field.Name, field.GetValue(thing));

        foreach (var property in thing.GetType().GetProperties(Todos))
        {
            if (property.GetIndexParameters().Length > 0) continue;
            try { Note(property.Name, property.GetValue(thing)); } catch { }
        }

        return values;
    }

    /// <summary>Cómo se llama de verdad cada miembro, para dejar de adivinar.</summary>
    public static List<string> Members()
    {
        var metadata = LibCpp2IlMain.TheMetadata!;
        object header = metadata.GetType()
            .GetField("metadataHeader", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(metadata) ?? new object();

        const BindingFlags Todos = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var names = new List<string> { "— cabecera: " + header.GetType().Name + " —" };
        names.AddRange(header.GetType().GetFields(Todos).Select(f => "  campo " + f.Name + " : " + f.FieldType.Name));
        names.AddRange(header.GetType().GetProperties(Todos).Select(p => "  prop  " + p.Name + " : " + p.PropertyType.Name));

        var definition = metadata.typeDefs.FirstOrDefault();
        if (definition != null)
        {
            names.Add("— tipo: " + definition.GetType().Name + " —");
            names.AddRange(definition.GetType().GetFields(Todos).Select(f => "  campo " + f.Name + " : " + f.FieldType.Name));
        }
        return names;
    }

    /// <summary>
    /// Las regiones que declara la cabecera, emparejando cada «…Offset» con su «…Count».
    ///
    /// El convenio de IL2CPP es que van de dos en dos y con el mismo prefijo. Donde no haya pareja
    /// se deja fuera en vez de inventarse un tamaño.
    /// </summary>
    public static List<Region> Regions()
    {
        // Cada región es un objeto Il2CppGlobalMetadataSectionHeader con su desplazamiento y su
        // tamaño dentro. La primera versión buscaba parejas de enteros «…Offset»/«…Count» sueltos
        // en la cabecera y no encontró ninguna: en esta versión de metadatos no están así.
        const BindingFlags Todos = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        var metadata = LibCpp2IlMain.TheMetadata!;
        object header = metadata.GetType().GetField("metadataHeader", Todos)?.GetValue(metadata)
                        ?? throw new InvalidOperationException("no encuentro metadataHeader");

        var regions = new List<Region>();
        foreach (var field in header.GetType().GetFields(Todos))
        {
            if (!field.FieldType.Name.Contains("SectionHeader", StringComparison.Ordinal)) continue;

            object? section = field.GetValue(header);
            if (section == null) continue;

            var numbers = Numbers(section);
            long offset = numbers.GetValueOrDefault("offset", numbers.GetValueOrDefault("Offset"));
            long size = numbers.GetValueOrDefault("size", numbers.GetValueOrDefault("Size"));
            if (offset > 0) regions.Add(new Region(field.Name, offset, size));
        }

        return regions.OrderBy(r => r.Offset).ToList();
    }

    /// <summary>Una clase de tres letras y el nombre real que lleva escondido dentro.</summary>
    public sealed record Pair(string Opcode, string Real);

    /// <summary>
    /// El enlace: los nombres reales son VALORES POR DEFECTO de campos.
    ///
    /// El bloque de 1,7 MB cae dentro de <c>fieldAndParameterDefaultValueData</c>, y esa tabla no
    /// está suelta: se indexa por campo. O sea que cada
    /// <c>Com.Ankama.Dofus.Server.Game.Protocol.Character.CharacterExperienceGainEvent|Types</c> es
    /// el valor por defecto de un campo concreto, y ese campo pertenece a una clase concreta —la de
    /// tres letras—. Eso es exactamente la pareja que faltaba.
    ///
    /// Antes probé tres caminos y ninguno valía: no son literales que cargue el código, no son los
    /// nombres de tipo vivos, y los tipos anidados también están ofuscados. Lo que no había mirado
    /// es de dónde salen, y salían de aquí.
    /// </summary>
    public static List<Pair> Pairs(ClientReader client, Action<string>? report = null)
    {
        var metadata = LibCpp2IlMain.TheMetadata
                       ?? throw new InvalidOperationException("no hay metadatos cargados");

        const BindingFlags Todos = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var pairs = new List<Pair>();

        // Las dos tablas que apuntan a esos datos: la de campos y la de parámetros. Se miran las
        // dos porque la región se llama «fieldAndParameter…» y no dice cuál de las dos la llena.
        foreach (string table in new[] { "fieldDefaultValues", "parameterDefaultValues" })
        {
            var array = metadata.GetType().GetField(table, Todos)?.GetValue(metadata) as Array;
            if (array == null) { report?.Invoke($"  {table}: no existe"); continue; }

            int hits = 0, sample = 0;
            foreach (object? entry in array)
            {
                if (entry == null) continue;
                var numbers = Numbers(entry);
                long data = numbers.GetValueOrDefault("dataIndex", -1);
                if (data < 0) continue;

                string? text;
                try { text = metadata.GetType()
                        .GetMethod("GetDefaultValue", Todos)
                        ?.Invoke(metadata, new object[] { (int)data, numbers.GetValueOrDefault("typeIndex") }) as string; }
                catch { text = null; }

                if (text == null || !text.StartsWith(Names.Prefix, StringComparison.Ordinal)) continue;
                hits++;
                if (sample++ < 3) report?.Invoke($"    {table}: {text}");
            }
            report?.Invoke($"  {table}: {array.Length:N0} entradas, {hits:N0} con nombre real");
        }

        return pairs;
    }

    /// <summary>El nombre que la tabla de tipos le da al tipo, y el índice del que sale.</summary>
    public sealed record Named(int Index, int NameIndex, string Name, string Namespace);

    /// <summary>
    /// Los tipos en el ORDEN de la tabla, con el índice de cadena del que sale cada nombre.
    ///
    /// Es lo que hace falta para contestar la pregunta: si los índices de nombre de los mensajes van
    /// seguidos y en el mismo orden que el bloque de restos, la correspondencia es posicional.
    /// </summary>
    public static List<Named> Types(int limit = 0)
    {
        var metadata = LibCpp2IlMain.TheMetadata
                       ?? throw new InvalidOperationException("no hay metadatos cargados");

        var definitions = metadata.typeDefs;
        var named = new List<Named>();

        for (int i = 0; i < definitions.Length; i++)
        {
            if (limit > 0 && named.Count >= limit) break;
            var definition = definitions[i];

            int nameIndex = (int)(definition.GetType()
                .GetField("nameIndex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(definition) ?? -1);

            named.Add(new Named(i, nameIndex, definition.Name ?? "", definition.Namespace ?? ""));
        }

        return named;
    }
}
