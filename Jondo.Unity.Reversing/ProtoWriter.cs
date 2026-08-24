using System.Reflection;
using System.Text;

namespace Jondo.Unity.Reversing;

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
    public sealed record Field(int Number, string Type, string Name, bool Repeated,
                               bool Optional = false);
    public sealed record Message(string Name, List<Field> Fields, bool Doubtful);
    public sealed record Enumeration(string Name, List<(string Name, int Value)> Values);

    private const BindingFlags Everything = BindingFlags.Public | BindingFlags.NonPublic |
                                            BindingFlags.Instance | BindingFlags.Static |
                                            BindingFlags.DeclaredOnly;

    /// <summary>
    /// El protocolo de un ensamblado, mensajes y enumerados, listo para emparejar.
    ///
    /// Abre, lee y cierra. Es lo que necesita todo el que quiera trabajar con una versión —la
    /// línea de comandos, el emparejador, la interfaz— y estaba escrito tres veces.
    /// </summary>
    public static Matcher.Model Model(string assemblyPath)
    {
        using var reader = new AssemblyReader(assemblyPath);
        return new Matcher.Model(Messages(reader), Enums(reader));
    }

    /// <summary>Los mensajes de protobuf que hay en el ensamblado.</summary>
    public static List<Message> Messages(AssemblyReader reader)
    {
        var messages = new List<Message>();

        // Obfuscated client messages have globally unique three-letter names.  A normal semantic
        // protobuf assembly does not: nested messages such as Character or Entry can legitimately
        // appear under several parents.  Keep the short name where it is unambiguous (which
        // preserves every existing client-to-client mapping), and qualify only collisions.
        var wireTypes = reader.Types().Where(type => type.IsEnum || IsMessage(type)).ToList();
        var messageTypes = wireTypes.Where(IsMessage).ToList();
        var identities = Identities(wireTypes);

        foreach (var type in messageTypes)
        {
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
            // Hay dos propiedades auxiliares que NO viajan:
            //
            //   - cada scalar `optional` lleva detrás un Boolean de sólo lectura (`HasFoo` en un
            //     ensamblado sin ofuscar);
            //   - cada oneof termina en un enumerado de sólo lectura que dice qué caso está puesto.
            //
            // Los nombres están rotados y no permiten reconocer `HasFoo`, pero la forma generada
            // sí: todo campo normal tiene setter; repeated/map son las únicas propiedades de campo
            // que son de sólo lectura. Con ese filtro las cuentas cuadran exactamente en los 2.169
            // mensajes Game de 3.6.10.10, incluidos oneof y los 381 indicadores de presencia.
            var allProperties = type.GetProperties(Everything)
                                    .Where(p => p.PropertyType.Name is not "MessageDescriptor" &&
                                                !p.PropertyType.Name.StartsWith("MessageParser",
                                                                                StringComparison.Ordinal))
                                    .ToList();
            var properties = allProperties.Where(IsWireProperty).ToList();

            var fields = new List<Field>();
            int pairs = Math.Min(numbers.Count, properties.Count);
            for (int i = 0; i < pairs; i++)
            {
                if (numbers[i].GetRawConstantValue() is not int number) continue;
                var (name, repeated) = Describe(properties[i].PropertyType, identities);
                fields.Add(new Field(number, name, properties[i].Name, repeated,
                                     IsOptional(allProperties, properties[i])));
            }

            messages.Add(new Message(identities[type.FullName ?? type.Name], fields,
                                     numbers.Count != properties.Count));
        }

        return messages.OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Los enumerados, que es donde acaban las direcciones, los estados y los motivos.</summary>
    public static List<Enumeration> Enums(AssemblyReader reader)
    {
        var enums = new List<Enumeration>();
        var wireTypes = reader.Types().Where(type => type.IsEnum || IsMessage(type)).ToList();
        var identities = Identities(wireTypes);

        foreach (var type in wireTypes)
        {
            if (!type.IsEnum) continue;

            var values = new List<(string, int)>();
            foreach (var f in type.GetFields(Everything).Where(f => f.IsLiteral))
            {
                if (f.GetRawConstantValue() is int v) values.Add((f.Name, v));
            }
            if (values.Count > 0)
                enums.Add(new Enumeration(identities[type.FullName ?? type.Name], values));
        }

        return enums.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
    }

    private static Dictionary<string, string> Identities(IEnumerable<Type> types)
    {
        var list = types.ToList();
        var duplicateNames = list
            .GroupBy(type => type.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        return list.ToDictionary(
            type => type.FullName ?? type.Name,
            type => duplicateNames.Contains(type.Name) ? type.FullName ?? type.Name : type.Name,
            StringComparer.Ordinal);
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

    private static bool IsWireProperty(PropertyInfo property)
    {
        if (property.CanWrite) return true;
        if (!property.PropertyType.IsGenericType) return false;

        string open = property.PropertyType.Name;
        return open.StartsWith("RepeatedField", StringComparison.Ordinal) ||
               open.StartsWith("MapField", StringComparison.Ordinal);
    }

    /// <summary>
    /// Un scalar opcional va seguido inmediatamente por su Boolean de presencia, que es de sólo
    /// lectura. No se usa el nombre porque Ankama lo rota igual que todos los demás.
    /// </summary>
    private static bool IsOptional(IReadOnlyList<PropertyInfo> properties, PropertyInfo property)
    {
        int index = -1;
        for (int i = 0; i < properties.Count; i++)
        {
            if (properties[i] == property) { index = i; break; }
        }

        if (index < 0 || index + 1 >= properties.Count) return false;
        PropertyInfo presence = properties[index + 1];
        return presence.PropertyType.Name == "Boolean" && !presence.CanWrite &&
               !IsWireProperty(presence);
    }

    /// <summary>Cómo se llama ese tipo en un .proto, y si es una lista.</summary>
    private static (string Name, bool Repeated) Describe(
        Type type, IReadOnlyDictionary<string, string>? messageIdentities = null)
    {
        if (type.IsGenericType)
        {
            string open = type.Name;
            var args = type.GetGenericArguments();
            if (open.StartsWith("RepeatedField", StringComparison.Ordinal))
                return (Describe(args[0], messageIdentities).Name, true);
            if (open.StartsWith("MapField", StringComparison.Ordinal))
                return ($"map<{Describe(args[0], messageIdentities).Name}, " +
                        $"{Describe(args[1], messageIdentities).Name}>", false);
        }

        if (messageIdentities != null && type.FullName != null &&
            messageIdentities.TryGetValue(type.FullName, out string? identity))
            return (identity, false);

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
                string cardinality = f.Repeated ? "repeated " : f.Optional ? "optional " : "";
                sb.AppendLine($"  {cardinality}{f.Type} {f.Name} = {f.Number};");
            }
            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
