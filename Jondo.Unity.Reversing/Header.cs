using System.Reflection;
using LibCpp2IL;

namespace Jondo.Unity.Reversing;

/// <summary>
/// The header of global-metadata.dat, read from LibCpp2IL itself.
///
/// The 1.7 MB block with the protocol's real names is in the file but nobody
/// references it: neither does the code load it as a literal, nor is it the live type table. The question left is
/// whether it is a REMNANT of the table from before the obfuscation and, above all, whether it keeps the ORDER. If it
/// keeps it, the pairing comes out by position and the problem is over.
///
/// To answer it one has to know which region of the file is which, and that is what the header says.
/// It is not parsed by hand: LibCpp2IL already has it read, so it is asked. The fields are taken by
/// reflection on purpose —they change name and order between metadata versions— and that way the probe
/// still works three patches from now.
/// </summary>
public static class Header
{
    /// <summary>A region of the file: where it starts and how much it takes up.</summary>
    public sealed record Region(string Name, long Offset, long Size)
    {
        public bool Holds(long position) => position >= Offset && position < Offset + Size;
    }

    /// <summary>Everything the header says, raw.</summary>
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
    /// The numbers an object carries inside, be they fields or properties.
    ///
    /// Both are looked at because guessing which is costly: the first version only read
    /// public fields and did not find a single one, and the result —«0 regions declared»— looked like a
    /// finding when it was the probe looking in the wrong place.
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

    /// <summary>What each member is really called, to stop guessing.</summary>
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
    /// The regions the header declares, pairing each «…Offset» with its «…Count».
    ///
    /// IL2CPP's convention is that they go in twos and with the same prefix. Where there is no pair
    /// it is left out instead of inventing a size.
    /// </summary>
    public static List<Region> Regions()
    {
        // Each region is an Il2CppGlobalMetadataSectionHeader object with its offset and its
        // size inside. The first version looked for loose «…Offset»/«…Count» integer pairs
        // in the header and did not find any: in this metadata version they are not like that.
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

    /// <summary>A three-letter class and the real name it carries hidden inside.</summary>
    public sealed record Pair(string Opcode, string Real);

    /// <summary>
    /// The link: the real names are DEFAULT VALUES of fields.
    ///
    /// The 1.7 MB block falls inside <c>fieldAndParameterDefaultValueData</c>, and that table is not
    /// loose: it is indexed by field. That is, each
    /// <c>Com.Ankama.Dofus.Server.Game.Protocol.Character.CharacterExperienceGainEvent|Types</c> is
    /// the default value of a specific field, and that field belongs to a specific class —the
    /// three-letter one—. That is exactly the pairing that was missing.
    ///
    /// Before, I tried three paths and none worked: they are not literals the code loads, they are not the
    /// live type names, and the nested types are obfuscated too. What I had not looked at
    /// is where they come from, and they came from here.
    /// </summary>
    public static List<Pair> Pairs(ClientReader client, Action<string>? report = null)
    {
        var metadata = LibCpp2IlMain.TheMetadata
                       ?? throw new InvalidOperationException("no hay metadatos cargados");

        const BindingFlags Todos = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var pairs = new List<Pair>();

        // The two tables pointing to that data: the fields one and the parameters one. Both are
        // looked at because the region is called «fieldAndParameter…» and does not say which of the two fills it.
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

    /// <summary>The name the type table gives the type, and the index it comes from.</summary>
    public sealed record Named(int Index, int NameIndex, string Name, string Namespace);

    /// <summary>
    /// The types in the table's ORDER, with the string index each name comes from.
    ///
    /// It is what is needed to answer the question: if the messages' name indices go
    /// in a row and in the same order as the block of remnants, the correspondence is positional.
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
