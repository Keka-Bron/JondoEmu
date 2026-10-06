using System.Reflection;
using System.Text;

namespace Jondo.Unity.Reversing;

/// <summary>
/// The client's .proto, rebuilt from its own classes.
///
/// The serialised descriptor is nowhere to be found —neither in the metadata nor in the binary— but
/// it is not needed: protobuf's C# generator leaves in each class everything necessary, and Cpp2IL
/// dumps it as is. A message class is recognised like this:
///
///     class jsd : IMessage&lt;jsd&gt;, IBufferMessage
///         const int  epvu = 1   epvw = 2   epvy = 3     ← the field numbers
///         static MessageParser&lt;jsd&gt; epvs                ← the parser
///         UnknownFieldSet epvt                          ← what it does not recognise
///         lbo epvv     Int64 epvx     lbo epvz          ← one field per number, in order
///
/// The names are rotated —epvu, epvv— and that does not matter: what is needed to match two
/// versions and to decode the wire are the NUMBERS and the TYPES, and those are whole.
///
/// The matching is by position: the generator always emits the number's constant right
/// before the field that uses it, so the nth number goes with the nth field. When the
/// counts do not add up, the message is flagged and nothing is invented.
/// </summary>
public static class ProtoWriter
{
    public sealed record Field(int Number, string Type, string Name, bool Repeated);
    public sealed record Message(string Name, List<Field> Fields, bool Doubtful);
    public sealed record Enumeration(string Name, List<(string Name, int Value)> Values);

    private const BindingFlags Everything = BindingFlags.Public | BindingFlags.NonPublic |
                                            BindingFlags.Instance | BindingFlags.Static |
                                            BindingFlags.DeclaredOnly;

    /// <summary>
    /// An assembly's protocol, messages and enums, ready to match.
    ///
    /// It opens, reads and closes. It is what everyone who wants to work with a version needs —the
    /// command line, the matcher, the interface— and it was written three times.
    /// </summary>
    public static Matcher.Model Model(string assemblyPath)
    {
        using var reader = new AssemblyReader(assemblyPath);
        return new Matcher.Model(Messages(reader), Enums(reader));
    }

    /// <summary>The protobuf messages in the assembly.</summary>
    public static List<Message> Messages(AssemblyReader reader)
    {
        var messages = new List<Message>();

        foreach (var type in reader.Types())
        {
            if (!IsMessage(type)) continue;

            // The literal's type comes from the metadata context, not from the runtime here, so
            // it is compared by name and not with typeof.
            var numbers = type.GetFields(Everything)
                              .Where(f => f.IsLiteral && f.FieldType.Name == "Int32")
                              .ToList();

            // The numbers are paired with the PROPERTIES, not with the backing fields.
            //
            // With a normal message it makes no difference: there is one field per property. But as soon as
            // a oneof appears there no longer is, because protobuf keeps all its cases in ONE single
            // Object field plus an enum saying which one is set. There the fields are two and the numbers
            // three, four or however many, and that is why two hundred and forty-two messages came out
            // mismatched: they were the ones with a oneof, not the ones misread.
            //
            // The properties do go one per number, and on top of that they carry each case's right type.
            // In front there are always the three standard ones —the parser and the two descriptors— and
            // behind, when there is a oneof, one is left over: the one saying which is set.
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

    /// <summary>The enums, which is where the directions, the states and the reasons end up.</summary>
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

    /// <summary>What that type is called in a .proto, and whether it is a list.</summary>
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

    /// <summary>Writes it all as a .proto that can be read and compared.</summary>
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
