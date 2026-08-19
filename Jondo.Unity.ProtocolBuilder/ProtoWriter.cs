using System.Reflection;
using System.Text;

namespace Jondo.Unity.ProtocolBuilder;

/// <summary>
/// El .proto del cliente, reconstruido de sus propias clases.
///
/// El descriptor serializado no está por ninguna parte —ni en los metadatos ni en el binario— pero
/// no hace falta: el generador de C# de protobuf deja en cada clase todo lo necesario, y Cpp2IL lo
/// vuelca tal cual. Una clase de mensaje se reconoce así:
///
///     class jsd : IMessage&lt;jsd&gt;, IBufferMessage
///         const int  epvu = 1   epvw = 2   epvy = 3     ← los números de campo
///         static MessageParser&lt;jsd&gt; epvs                ← el analizador
///         UnknownFieldSet epvt                          ← lo que no reconoce
///         lbo epvv     Int64 epvx     lbo epvz          ← un campo por número, en orden
///
/// Los nombres están rotados —epvu, epvv— y eso da igual: lo que hace falta para emparejar dos
/// versiones y para descodificar el cable son los NÚMEROS y los TIPOS, y ésos están enteros.
///
/// El emparejamiento es por posición: el generador emite siempre la constante del número justo
/// antes del campo que la usa, así que el enésimo número va con el enésimo campo. Cuando las
/// cuentas no cuadran, el mensaje se marca y no se inventa nada.
/// </summary>
public static class ProtoWriter
{
    public sealed record Field(int Number, string Type, string Name, bool Repeated);
    public sealed record Message(string Name, List<Field> Fields, bool Doubtful);
    public sealed record Enumeration(string Name, List<(string Name, int Value)> Values);

    private const BindingFlags Everything = BindingFlags.Public | BindingFlags.NonPublic |
                                            BindingFlags.Instance | BindingFlags.Static |
                                            BindingFlags.DeclaredOnly;

    /// <summary>Los mensajes de protobuf que hay en el ensamblado.</summary>
    public static List<Message> Messages(AssemblyReader reader)
    {
        var messages = new List<Message>();

        foreach (var type in reader.Types())
        {
            if (!IsMessage(type)) continue;

            // El tipo del literal viene del contexto de metadatos, no del runtime de aquí, así que
            // se compara por nombre y no con typeof.
            var numbers = type.GetFields(Everything)
                              .Where(f => f.IsLiteral && f.FieldType.Name == "Int32")
                              .ToList();

            // Los números se emparejan con las PROPIEDADES, no con los campos de respaldo.
            //
            // Con un mensaje normal da igual: hay un campo por propiedad. Pero en cuanto aparece
            // un oneof deja de haberlo, porque protobuf guarda todos sus casos en UN solo campo
            // Object más un enumerado con el que esté puesto. Ahí los campos son dos y los números
            // tres, cuatro o los que sean, y por eso doscientos cuarenta y dos mensajes salían
            // descuadrados: eran los que tienen oneof, no los que estaban mal leídos.
            //
            // Las propiedades sí van una por número, y encima llevan el tipo bueno de cada caso.
            // Delante están siempre las tres de oficio —el analizador y los dos descriptores— y
            // detrás, cuando hay oneof, sobra una: la que dice cuál está puesto.
            var properties = type.GetProperties(Everything)
                                 .Where(p => p.PropertyType.Name is not "MessageDescriptor" &&
                                             !p.PropertyType.Name.StartsWith("MessageParser",
                                                                             StringComparison.Ordinal))
                                 .ToList();

            var fields = new List<Field>();
            int pairs = Math.Min(numbers.Count, properties.Count);
            for (int i = 0; i < pairs; i++)
            {
                if (numbers[i].GetRawConstantValue() is not int number) continue;
                var (name, repeated) = Describe(properties[i].PropertyType);
                fields.Add(new Field(number, name, properties[i].Name, repeated));
            }

            messages.Add(new Message(type.Name, fields, numbers.Count > properties.Count));
        }

        return messages.OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Los enumerados, que es donde acaban las direcciones, los estados y los motivos.</summary>
    public static List<Enumeration> Enums(AssemblyReader reader)
    {
        var enums = new List<Enumeration>();

        foreach (var type in reader.Types())
        {
            if (!type.IsEnum) continue;

            var values = new List<(string, int)>();
            foreach (var f in type.GetFields(Everything).Where(f => f.IsLiteral))
            {
                if (f.GetRawConstantValue() is int v) values.Add((f.Name, v));
            }
            if (values.Count > 0) enums.Add(new Enumeration(type.Name, values));
        }

        return enums.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
    }

    private static bool IsMessage(Type type)
    {
        try
        {
            foreach (var i in type.GetInterfaces())
            {
                if (i.Name is "IBufferMessage" or "IMessage") return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Cómo se llama ese tipo en un .proto, y si es una lista.</summary>
    private static (string Name, bool Repeated) Describe(Type type)
    {
        if (type.IsGenericType)
        {
            string open = type.Name;
            var args = type.GetGenericArguments();
            if (open.StartsWith("RepeatedField", StringComparison.Ordinal))
                return (Describe(args[0]).Name, true);
            if (open.StartsWith("MapField", StringComparison.Ordinal))
                return ($"map<{Describe(args[0]).Name}, {Describe(args[1]).Name}>", false);
        }

        return (type.Name switch
        {
            "Int32" => "int32",
            "Int64" => "int64",
            "UInt32" => "uint32",
            "UInt64" => "uint64",
            "Boolean" => "bool",
            "String" => "string",
            "Single" => "float",
            "Double" => "double",
            "ByteString" => "bytes",
            _ => type.Name,
        }, false);
    }

    /// <summary>Lo escribe todo como un .proto que se puede leer y comparar.</summary>
    public static string Write(IEnumerable<Message> messages, IEnumerable<Enumeration> enums,
                               string source)
    {
        var sb = new StringBuilder();
        sb.AppendLine("syntax = \"proto3\";");
        sb.AppendLine();
        sb.AppendLine("// Reconstruido de las clases del propio cliente por Jondo.Unity.ProtocolBuilder.");
        sb.AppendLine($"// Origen: {source}");
        sb.AppendLine("//");
        sb.AppendLine("// Los nombres van rotados por Ankama; los números y los tipos son los de verdad.");
        sb.AppendLine();

        foreach (var e in enums)
        {
            sb.AppendLine($"enum {e.Name} {{");
            foreach (var (name, value) in e.Values) sb.AppendLine($"  {name} = {value};");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        foreach (var m in messages)
        {
            if (m.Doubtful) sb.AppendLine("// OJO: los números y los campos no cuadran en cuenta.");
            sb.AppendLine($"message {m.Name} {{");
            foreach (var f in m.Fields)
            {
                sb.AppendLine($"  {(f.Repeated ? "repeated " : "")}{f.Type} {f.Name} = {f.Number};");
            }
            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
