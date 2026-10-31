using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Jondo.Unity.ProtocolGenerator;

/// <summary>
/// Generates direct, typed conversions. Descriptors own protobuf structure; Roslyn
/// symbols own stable construction. The mapping contains semantic bindings only.
/// </summary>
public sealed class BindingGenerator
{
    private sealed class Mapping
    {
        public required string StableNamespace { get; init; }
        public required string GeneratedNamespace { get; init; }
        public required string ClassName { get; init; }
        public required string Root { get; init; }
        public Dictionary<string, MessageBinding> Messages { get; init; } = new();
        public Dictionary<string, EnumBinding> Enums { get; init; } = new();
    }

    private sealed class MessageBinding
    {
        public required string Name { get; init; }
        public Dictionary<string, string> Fields { get; init; } = new();
        public Dictionary<string, OneofBinding> Oneofs { get; init; } = new();
        public string[] IgnoredWireFields { get; init; } = [];
    }

    private sealed class OneofBinding
    {
        public required string Member { get; init; }
        public Dictionary<string, CaseBinding> Cases { get; init; } = new();
    }

    private sealed class CaseBinding
    {
        public required string Type { get; init; }
        public string? Member { get; init; }
    }

    private sealed class EnumBinding
    {
        public required string Name { get; init; }
        public Dictionary<string, string> Values { get; init; } = new();
    }

    private sealed record WireMessage(DescriptorProto Descriptor, string CSharpType);
    private sealed record WireEnum(EnumDescriptorProto Descriptor, string CSharpType);
    private readonly Mapping _mapping;
    private readonly Compilation _stable;
    private readonly Dictionary<string, WireMessage> _messages = new();
    private readonly Dictionary<string, WireEnum> _enums = new();
    private readonly Dictionary<WireMessage, MessageBinding> _messageBindings = new();
    private readonly Dictionary<WireEnum, EnumBinding> _enumBindings = new();
    private readonly StringBuilder _output = new();

    public static string Generate(FileDescriptorSet descriptors, string mappingJson, Compilation stable)
    {
        using var json = JsonDocument.Parse(mappingJson);
        CheckDuplicateKeys(json.RootElement);
        var mapping = JsonSerializer.Deserialize<Mapping>(mappingJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true
        }) ?? throw Error("The mapping is empty.");
        var generator = new BindingGenerator(descriptors, mapping, stable);
        generator.Validate();
        return generator.Emit();
    }

    private BindingGenerator(FileDescriptorSet descriptors, Mapping mapping, Compilation stable)
    {
        _mapping = mapping;
        _stable = stable;
        foreach (var file in descriptors.File)
        {
            string ns = file.Options?.CsharpNamespace ?? file.Package;
            foreach (var message in file.MessageType)
                IndexMessage(message, file.Package, $"global::{ns}");
            foreach (var item in file.EnumType)
                _enums.Add(Qualify(file.Package, item.Name), new(item, $"global::{ns}.{item.Name}"));
        }
        foreach (var (alias, binding) in mapping.Messages)
            if (!_messageBindings.TryAdd(Message(alias), binding)) throw Error($"Duplicate message binding: {alias}.");
        foreach (var (alias, binding) in mapping.Enums)
            if (!_enumBindings.TryAdd(Enum(alias), binding)) throw Error($"Duplicate enum binding: {alias}.");
    }

    private void IndexMessage(DescriptorProto message, string parent, string csharpParent)
    {
        string fullName = Qualify(parent, message.Name);
        string csharp = $"{csharpParent}.{message.Name}";
        _messages.Add(fullName, new(message, csharp));
        foreach (var child in message.NestedType) IndexMessage(child, fullName, csharp + ".Types");
        foreach (var item in message.EnumType)
            _enums.Add(Qualify(fullName, item.Name), new(item, $"{csharp}.Types.{item.Name}"));
    }

    private void Validate()
    {
        foreach (string ns in new[] { _mapping.StableNamespace, _mapping.GeneratedNamespace })
            if (!ns.Split('.').All(SyntaxFacts.IsValidIdentifier)) throw Error($"Invalid namespace: {ns}.");
        if (!SyntaxFacts.IsValidIdentifier(_mapping.ClassName)) throw Error("Invalid generated class name.");
        var identities = _messageBindings.Values.Select(b => b.Name).Concat(_enumBindings.Values.Select(b => b.Name)).ToArray();
        if (identities.Distinct().Count() != identities.Length) throw Error("Duplicate stable type bindings.");
        if (_messageBindings.Values.Count(b => b.Name == _mapping.Root) != 1)
            throw Error($"Root {_mapping.Root} needs exactly one message binding.");

        foreach (var (wire, binding) in _enumBindings)
        {
            var stable = Stable(binding.Name);
            if (stable.TypeKind != TypeKind.Enum) throw Error($"{binding.Name} must be an enum.");
            var stableValues = stable.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).ToArray();
            if (stableValues.Select(f => f.ConstantValue).Distinct().Count() != stableValues.Length)
                throw Error($"Stable enum {binding.Name} contains numeric aliases.");
            if (binding.Values.Values.Distinct().Count() != binding.Values.Count ||
                !stableValues.Select(v => v.Name).ToHashSet().SetEquals(binding.Values.Values))
                throw Error($"Incomplete or duplicate stable enum construction: {binding.Name}.");
            foreach (var (alias, target) in binding.Values)
                if (!wire.Descriptor.Value.Any(v => v.Name == alias)) throw Error($"Unknown enum value {wire.Descriptor.Name}.{alias} -> {target}.");
            var numbers = binding.Values.Keys.Select(k => wire.Descriptor.Value.Single(v => v.Name == k).Number);
            if (numbers.Distinct().Count() != binding.Values.Count) throw Error($"Ambiguous numeric aliases in {wire.Descriptor.Name}.");
        }

        foreach (var (wire, binding) in _messageBindings)
        {
            var stable = Stable(binding.Name);
            bool self = binding.Oneofs.Values.Any(o => o.Member == "$self");
            var parameters = self ? [] : Constructor(stable).Parameters.ToArray();
            if (self && (!stable.IsAbstract || binding.Oneofs.Count != 1 || binding.Fields.Count != 0))
                throw Error($"{binding.Name}: $self requires an abstract type and exactly one oneof binding.");
            var members = new HashSet<string>();
            foreach (var (name, member) in binding.Fields)
            {
                var field = Field(wire, name);
                if (RealOneof(field)) throw Error($"{wire.Descriptor.Name}.{name} must be bound through its oneof.");
                if (!members.Add(member)) throw Error($"Duplicate stable member: {binding.Name}.{member}.");
                ValidateValue(field, Parameter(parameters, member).Type);
            }
            foreach (string name in binding.IgnoredWireFields)
            {
                var field = Field(wire, name);
                if (RealOneof(field) || binding.Fields.ContainsKey(name)) throw Error($"Invalid ignored field: {wire.Descriptor.Name}.{name}.");
            }
            if (binding.IgnoredWireFields.Distinct().Count() != binding.IgnoredWireFields.Length)
                throw Error($"Duplicate ignored field in {wire.Descriptor.Name}.");
            foreach (var field in wire.Descriptor.Field.Where(f => !RealOneof(f)))
                if (!binding.Fields.ContainsKey(field.Name) && !binding.IgnoredWireFields.Contains(field.Name))
                    throw Error($"Unbound wire field {wire.Descriptor.Name}.{field.Name}; bind it or explicitly ignore it.");

            foreach (var (name, oneof) in binding.Oneofs)
            {
                int index = wire.Descriptor.OneofDecl.ToList().FindIndex(o => o.Name == name);
                if (index < 0 || !wire.Descriptor.Field.Any(f => RealOneof(f) && f.OneofIndex == index))
                    throw Error($"Unknown real oneof {wire.Descriptor.Name}.{name}.");
                if (!members.Add(oneof.Member)) throw Error($"Duplicate stable member: {binding.Name}.{oneof.Member}.");
                var union = self ? stable : Parameter(parameters, oneof.Member).Type as INamedTypeSymbol;
                if (union is null || !union.IsAbstract || union.NullableAnnotation == NullableAnnotation.Annotated)
                    throw Error($"{binding.Name}.{oneof.Member} must be a required abstract case type.");
                var cases = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
                foreach (var (alias, branch) in oneof.Cases)
                {
                    var field = Field(wire, alias);
                    if (!RealOneof(field) || field.OneofIndex != index) throw Error($"{alias} does not belong to oneof {name}.");
                    var target = Stable(branch.Type);
                    if (target.IsAbstract || !Inherits(target, union) || !cases.Add(target))
                        throw Error($"Invalid or duplicate case {branch.Type} for {union.Name}.");
                    if (branch.Member is null) ValidateValue(field, target);
                    else
                    {
                        var args = Constructor(target).Parameters;
                        if (args.Length != 1 || args[0].Name != branch.Member)
                            throw Error($"Case wrapper {branch.Type} must have exactly one member named {branch.Member}.");
                        ValidateValue(field, args[0].Type);
                    }
                }
                var descendants = Types(union.ContainingAssembly.GlobalNamespace).Where(t => !t.IsAbstract && Inherits(t, union));
                if (!cases.SetEquals(descendants)) throw Error($"Incomplete stable cases for {union.Name} in {wire.Descriptor.Name}.{name}.");
            }
            foreach (var field in wire.Descriptor.Field.Where(RealOneof))
                if (!binding.Oneofs.ContainsKey(wire.Descriptor.OneofDecl[field.OneofIndex].Name))
                    throw Error($"Unbound wire oneof in {wire.Descriptor.Name}: {field.Name}.");
            if (!self && !members.SetEquals(parameters.Select(p => p.Name)))
                throw Error($"Incomplete stable construction for {binding.Name}.");
        }
    }

    private void ValidateValue(FieldDescriptorProto field, ITypeSymbol target)
    {
        bool repeated = field.Label == FieldDescriptorProto.Types.Label.Repeated;
        var element = Element(target);
        if (repeated != (element is not null)) throw Error($"Repetition mismatch for {field.Name} -> {target}.");
        if (repeated)
        {
            if (target.NullableAnnotation == NullableAnnotation.Annotated) throw Error($"Repeated member {field.Name} cannot be nullable.");
            target = element!;
        }
        if (field.Proto3Optional)
        {
            if (target is not INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
                throw Error($"Optional scalar {field.Name} needs a nullable stable value.");
            target = nullable.TypeArguments[0];
        }
        else if (target is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
            throw Error($"{field.Name} has no optional scalar presence.");
        if (repeated && target.NullableAnnotation == NullableAnnotation.Annotated)
            throw Error($"Repeated elements of {field.Name} cannot be nullable.");

        ITypeSymbol expected;
        if (field.Type == FieldDescriptorProto.Types.Type.Message)
        {
            var wire = Message(field.TypeName);
            if (wire.Descriptor.Options?.MapEntry == true) throw Error("Map bindings are not supported by this generator.");
            if (!_messageBindings.TryGetValue(wire, out var binding)) throw Error($"Missing message binding for {field.TypeName}.");
            expected = Stable(binding.Name);
        }
        else if (field.Type == FieldDescriptorProto.Types.Type.Enum)
        {
            var wire = Enum(field.TypeName);
            if (!_enumBindings.TryGetValue(wire, out var binding)) throw Error($"Missing enum binding for {field.TypeName}.");
            expected = Stable(binding.Name);
        }
        else
        {
            var special = field.Type switch
            {
                FieldDescriptorProto.Types.Type.Int32 or FieldDescriptorProto.Types.Type.Sint32 or FieldDescriptorProto.Types.Type.Sfixed32 => SpecialType.System_Int32,
                FieldDescriptorProto.Types.Type.Int64 or FieldDescriptorProto.Types.Type.Sint64 or FieldDescriptorProto.Types.Type.Sfixed64 => SpecialType.System_Int64,
                FieldDescriptorProto.Types.Type.Uint32 or FieldDescriptorProto.Types.Type.Fixed32 => SpecialType.System_UInt32,
                FieldDescriptorProto.Types.Type.Uint64 or FieldDescriptorProto.Types.Type.Fixed64 => SpecialType.System_UInt64,
                FieldDescriptorProto.Types.Type.Bool => SpecialType.System_Boolean,
                FieldDescriptorProto.Types.Type.String => SpecialType.System_String,
                FieldDescriptorProto.Types.Type.Float => SpecialType.System_Single,
                FieldDescriptorProto.Types.Type.Double => SpecialType.System_Double,
                _ => throw Error($"Unsupported scalar type: {field.Type}.")
            };
            expected = _stable.GetSpecialType(special);
            if (target.NullableAnnotation == NullableAnnotation.Annotated) throw Error($"{field.Name} has no nullable scalar representation.");
        }
        if (!SymbolEqualityComparer.Default.Equals(expected, target)) throw Error($"Type mismatch for {field.Name}: expected {expected}, found {target}.");
    }

    private string Emit()
    {
        Line("// <auto-generated />");
        Line("#nullable enable");
        Line("using System;");
        Line("using System.Linq;");
        Line("using Google.Protobuf;");
        Line($"namespace {_mapping.GeneratedNamespace};");
        Line($"public static class {_mapping.ClassName}");
        Line("{");
        var root = _messageBindings.Single(p => p.Value.Name == _mapping.Root).Key;
        string stableRoot = Type(Stable(_mapping.Root));
        Line($"    public static {stableRoot} Decode(ReadOnlySpan<byte> payload) => ToStable({root.CSharpType}.Parser.ParseFrom(payload));");
        Line($"    public static byte[] Encode({stableRoot} value) => ToWire(value).ToByteArray();");
        Line($"    public static IMessage DecodeWire(ReadOnlySpan<byte> payload) => {root.CSharpType}.Parser.ParseFrom(payload);");
        Line($"    private static Exception Invalid(string path) => new global::{_mapping.StableNamespace}.ConnectionConversionException(\"Unsupported or missing protocol value: \" + path);");
        foreach (var (wire, binding) in _enumBindings) EmitEnum(wire, binding);
        foreach (var (wire, binding) in _messageBindings) EmitMessage(wire, binding);
        Line("}");
        return _output.ToString();
    }

    private void EmitEnum(WireEnum wire, EnumBinding binding)
    {
        string stable = Type(Stable(binding.Name));
        Line($"    internal static {stable} ToStable({wire.CSharpType} value) => value switch");
        Line("    {");
        foreach (var (alias, member) in binding.Values)
            Line($"        ({wire.CSharpType}){wire.Descriptor.Value.Single(v => v.Name == alias).Number} => {stable}.{member},");
        Line($"        _ => throw Invalid(\"{wire.Descriptor.Name}\" + \" enum \" + (int)value)");
        Line("    };");
        Line($"    internal static {wire.CSharpType} ToWire({stable} value) => value switch");
        Line("    {");
        foreach (var (alias, member) in binding.Values)
            Line($"        {stable}.{member} => ({wire.CSharpType}){wire.Descriptor.Value.Single(v => v.Name == alias).Number},");
        Line($"        _ => throw Invalid(\"{binding.Name}\" + \" enum \" + value)");
        Line("    };");
    }

    private void EmitMessage(WireMessage wire, MessageBinding binding)
    {
        var stable = Stable(binding.Name);
        bool self = binding.Oneofs.Values.Any(o => o.Member == "$self");
        var args = new Dictionary<string, string>();
        Line($"    internal static {Type(stable)} ToStable({wire.CSharpType} value)");
        Line("    {");
        Line($"        if (value is null) throw Invalid(\"{wire.Descriptor.Name}\");");
        foreach (var (name, member) in binding.Fields)
            args.Add(member, ReadValue(Field(wire, name), Constructor(stable).Parameters.Single(p => p.Name == member).Type, $"value.{Pascal(name)}"));
        foreach (var (name, oneof) in binding.Oneofs)
        {
            string expression = $"value.{Pascal(name)}Case switch\n        {{\n";
            foreach (var (alias, branch) in oneof.Cases)
            {
                var target = Stable(branch.Type);
                var fieldType = branch.Member is null ? target : Constructor(target).Parameters[0].Type;
                string value = ReadValue(Field(wire, alias), fieldType, $"value.{Pascal(alias)}");
                if (branch.Member is not null) value = $"new {Type(target)}({value})";
                expression += $"            {wire.CSharpType}.{Pascal(name)}OneofCase.{Pascal(alias)} => ({Type(self ? stable : Constructor(stable).Parameters.Single(p => p.Name == oneof.Member).Type)}){value},\n";
            }
            expression += $"            _ => throw Invalid(\"{wire.Descriptor.Name}.{name}\")\n        }}";
            args.Add(oneof.Member, expression);
        }
        if (self) Line($"        return {args["$self"]};");
        else Line($"        return new {Type(stable)}({string.Join(",\n            ", Constructor(stable).Parameters.Select(p => args[p.Name]))});");
        Line("    }");

        Line($"    internal static {wire.CSharpType} ToWire({Type(stable)} value)");
        Line("    {");
        Line($"        if (value is null) throw Invalid(\"{binding.Name}\");");
        Line($"        var result = new {wire.CSharpType}();");
        foreach (var (name, member) in binding.Fields)
        {
            var field = Field(wire, name);
            var type = Constructor(stable).Parameters.Single(p => p.Name == member).Type;
            string expression = WriteValue(field, type, $"value.{member}");
            if (field.Label == FieldDescriptorProto.Types.Label.Repeated) Line($"        result.{Pascal(name)}.Add({expression});");
            else if (field.Proto3Optional) Line($"        if (value.{member}.HasValue) result.{Pascal(name)} = {expression};");
            else if (field.Type == FieldDescriptorProto.Types.Type.Message && type.NullableAnnotation == NullableAnnotation.Annotated)
                Line($"        if (value.{member} is not null) result.{Pascal(name)} = {expression};");
            else Line($"        result.{Pascal(name)} = {expression};");
        }
        foreach (var (name, oneof) in binding.Oneofs)
        {
            Line($"        switch ({(self ? "value" : "value." + oneof.Member)})");
            Line("        {");
            foreach (var (alias, branch) in oneof.Cases)
            {
                var target = Stable(branch.Type);
                var type = branch.Member is null ? target : Constructor(target).Parameters[0].Type;
                string expression = WriteValue(Field(wire, alias), type, branch.Member is null ? "item" : "item." + branch.Member);
                Line($"            case {Type(target)} item: result.{Pascal(alias)} = {expression}; break;");
            }
            Line($"            default: throw Invalid(\"{binding.Name}.{oneof.Member}\");");
            Line("        }");
        }
        Line("        return result;");
        Line("    }");
    }

    private string ReadValue(FieldDescriptorProto field, ITypeSymbol type, string value, bool element = false)
    {
        if (!element && field.Label == FieldDescriptorProto.Types.Label.Repeated)
            return $"{value}.Select(item => {ReadValue(field, Element(type)!, "item", true)}).ToArray()";
        string converted = field.Type is FieldDescriptorProto.Types.Type.Message or FieldDescriptorProto.Types.Type.Enum
            ? $"ToStable({value})" : value;
        if (field.Proto3Optional)
            return $"value.Has{Pascal(field.Name)} ? ({Type(type)}){converted} : null";
        if (field.Type == FieldDescriptorProto.Types.Type.Message)
            return type.NullableAnnotation == NullableAnnotation.Annotated
                ? $"{value} is null ? null : {converted}"
                : $"{converted}";
        return converted;
    }

    private string WriteValue(FieldDescriptorProto field, ITypeSymbol type, string value, bool element = false)
    {
        if (!element && field.Label == FieldDescriptorProto.Types.Label.Repeated)
            return $"{value}.Select(item => {WriteValue(field, Element(type)!, "item", true)})";
        if (field.Proto3Optional) value += ".Value";
        return field.Type is FieldDescriptorProto.Types.Type.Message or FieldDescriptorProto.Types.Type.Enum ? $"ToWire({value})" : value;
    }

    private INamedTypeSymbol Stable(string name) => _stable.GetTypeByMetadataName(_mapping.StableNamespace + "." + name)
        ?? throw Error($"Unknown stable type: {name}.");

    private static IMethodSymbol Constructor(INamedTypeSymbol type)
    {
        var constructors = type.InstanceConstructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public &&
                        !(c.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, type)))
            .ToArray();
        if (!type.IsRecord || constructors.Length != 1)
            throw Error($"{type} must be a record with exactly one public semantic constructor.");
        foreach (var parameter in constructors[0].Parameters)
            if (!type.GetMembers(parameter.Name).OfType<IPropertySymbol>().Any(p =>
                    p.GetMethod?.DeclaredAccessibility == Accessibility.Public &&
                    SymbolEqualityComparer.IncludeNullability.Equals(p.Type, parameter.Type)))
                throw Error($"{type}.{parameter.Name} must expose its constructor value through a public property of the same type.");
        return constructors[0];
    }

    private static IParameterSymbol Parameter(IParameterSymbol[] args, string name) => args.SingleOrDefault(p => p.Name == name)
        ?? throw Error($"Unknown stable constructor member: {name}.");

    private WireMessage Message(string alias) => Resolve(_messages, alias, "message");
    private WireEnum Enum(string alias) => Resolve(_enums, alias, "enum");
    private static T Resolve<T>(Dictionary<string, T> values, string alias, string kind)
    {
        if (values.TryGetValue(alias.TrimStart('.'), out var found)) return found;
        var matches = values.Where(p => p.Key.EndsWith("." + alias, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1 ? matches[0].Value : throw Error($"Unknown or ambiguous wire {kind}: {alias}.");
    }

    private static FieldDescriptorProto Field(WireMessage message, string name) => message.Descriptor.Field.SingleOrDefault(f => f.Name == name)
        ?? throw Error($"Unknown wire field: {message.Descriptor.Name}.{name}.");
    private static bool RealOneof(FieldDescriptorProto field) => field.HasOneofIndex && !field.Proto3Optional;
    private static ITypeSymbol? Element(ITypeSymbol type) => type is INamedTypeSymbol { Name: "IReadOnlyList", TypeArguments.Length: 1 } list &&
        list.ContainingNamespace.ToDisplayString() == "System.Collections.Generic" ? list.TypeArguments[0] : null;
    private static string Type(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier | SymbolDisplayMiscellaneousOptions.UseSpecialTypes));
    private static bool Inherits(INamedTypeSymbol type, INamedTypeSymbol parent)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            if (SymbolEqualityComparer.Default.Equals(current, parent)) return true;
        return false;
    }
    private static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol ns) => ns.GetTypeMembers()
        .Concat(ns.GetNamespaceMembers().SelectMany(Types));
    private static string Qualify(string parent, string name) => parent.Length == 0 ? name : parent + "." + name;
    private static string Pascal(string value) => string.Concat(value.Split('_').Select(p => p.Length == 0 ? "" : char.ToUpperInvariant(p[0]) + p[1..]));
    private static InvalidOperationException Error(string message) => new(message);
    private void Line(string value) => _output.AppendLine(value);
    private static void CheckDuplicateKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Error($"Duplicate mapping key: {property.Name}.");
                CheckDuplicateKeys(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) CheckDuplicateKeys(child);
    }
}
