using Google.Protobuf.Reflection;

namespace Jondo.Unity.ProtocolGenerator;

public sealed partial class BindingGenerator
{
    private sealed class EnvelopeBinding
    {
        public required string Alias { get; init; }
        public Dictionary<string, string> Fields { get; init; } = new();
    }

    private (WireMessage Wire, FieldDescriptorProto Field) EnvelopeField(string role, string member)
    {
        var binding = _mapping.GameEnvelope![role];
        var wire = Message(binding.Alias);
        var fields = binding.Fields.Where(p => p.Value == member).ToArray();
        if (fields.Length != 1) throw Error($"Envelope {role} needs exactly one {member} binding.");
        return (wire, Field(wire, fields[0].Key));
    }

    private void ValidateGame()
    {
        foreach (var alias in _mapping.WireAliases.Keys) _ = Message(alias);
        var aliases = _messages.Values.Where(w => w.Descriptor.Options?.MapEntry != true).Select(WireAlias).ToArray();
        if (aliases.Distinct().Count() != aliases.Length) throw Error("Duplicate raw payload aliases.");
        if (_mapping.Root is not null || !_mapping.GameEnvelope!.Keys.ToHashSet().SetEquals(["Message", "Request", "Response", "Event"]))
            throw Error("Game requires exactly Message/Request/Response/Event envelope roles and no ordinary root.");
        var root = Message(_mapping.GameEnvelope["Message"].Alias);
        int? group = null;
        foreach (string role in new[] { "Request", "Response", "Event" })
        {
            var (_, branch) = EnvelopeField("Message", role.ToLowerInvariant());
            var wire = Message(_mapping.GameEnvelope[role].Alias);
            if (!RealOneof(branch) || branch.Type != FieldDescriptorProto.Types.Type.Message || Message(branch.TypeName) != wire ||
                (group is not null && group != branch.OneofIndex)) throw Error($"Invalid Game envelope branch {role}.");
            group = branch.OneofIndex;
            var (_, content) = EnvelopeField(role, "content");
            if (content.Type != FieldDescriptorProto.Types.Type.Message || content.TypeName.TrimStart('.') != "google.protobuf.Any" ||
                content.Label == FieldDescriptorProto.Types.Label.Repeated || RealOneof(content)) throw Error($"Invalid {role}.content.");
            if (role != "Event")
            {
                var (_, uid) = EnvelopeField(role, "uid");
                if (uid.Type != FieldDescriptorProto.Types.Type.Int32 || uid.Label == FieldDescriptorProto.Types.Label.Repeated || uid.HasOneofIndex)
                    throw Error($"Invalid {role}.uid: signed int32 required.");
            }
            var expected = role == "Event" ? new[] { "content" } : ["content", "uid"];
            if (!_mapping.GameEnvelope[role].Fields.Values.ToHashSet().SetEquals(expected)) throw Error($"Unknown envelope fields for {role}.");
        }
        if (_mapping.GameEnvelope["Message"].Fields.Count != 3 || root.Descriptor.Field.Count(RealOneof) != 3)
            throw Error("Game Message must bind all three envelope branches.");
        foreach (var (_, binding) in _messageBindings)
        {
            if (binding.Kind is null) continue;
            if (binding.Kind is not ("request" or "response" or "event")) throw Error($"Unknown endpoint kind {binding.Kind}.");
            if (!Inherits(Stable(binding.Name), Stable("Game" + Pascal(binding.Kind))))
                throw Error($"{binding.Name} must inherit Game{Pascal(binding.Kind)}.");
        }
    }

    private void EmitGame()
    {
        string ns = "global::" + _mapping.StableNamespace + ".";
        var root = Message(_mapping.GameEnvelope!["Message"].Alias);
        var (_, first) = EnvelopeField("Message", "event");
        string group = Pascal(root.Descriptor.OneofDecl[first.OneofIndex].Name);
        Line($"    public static IMessage DecodeWire(ReadOnlySpan<byte> payload) => {root.CSharpType}.Parser.ParseFrom(payload);");
        Line($"    public static GameWireEnvelope ReadEnvelope(ReadOnlySpan<byte> payload)");
        Line("    {");
        Line($"        var value = {root.CSharpType}.Parser.ParseFrom(payload);");
        Line($"        switch (value.{group}Case)");
        Line("        {");
        foreach (string role in new[] { "Request", "Response", "Event" })
        {
            var (_, branch) = EnvelopeField("Message", role.ToLowerInvariant());
            var (_, content) = EnvelopeField(role, "content");
            string access = "value." + Pascal(branch.Name);
            string uid = role == "Event" ? "null" : access + "." + Pascal(EnvelopeField(role, "uid").Field.Name);
            Line($"            case {root.CSharpType}.{group}OneofCase.{Pascal(branch.Name)}:");
            Line($"                return new({ns}GameMessageKind.{role}, {uid}, {access}.{Pascal(content.Name)} ?? throw Invalid(\"{role}.content\"));");
        }
        Line("            default: throw Invalid(\"Message.kind\");");
        Line("        }");
        Line("    }");
        Line($"    public static {ns}GameFrame Decode(ReadOnlySpan<byte> payload)");
        Line("    {");
        Line("        var envelope = ReadEnvelope(payload);");
        Line("        var content = DecodePayload(envelope.Alias, envelope.Content.Value.Span);");
        Line("        if (Kind(content) != envelope.Kind) throw Invalid(\"Envelope/payload kind mismatch\");");
        Line("        return new(content, envelope.Uid);");
        Line("    }");
        Line($"    public static {ns}GamePayload DecodePayload(string alias, ReadOnlySpan<byte> payload) => alias switch");
        Line("    {");
        foreach (var (wire, binding) in _messageBindings.Where(p => p.Value.Kind is not null))
            Line($"        \"{WireAlias(wire)}\" => ToStable({wire.CSharpType}.Parser.ParseFrom(payload)),");
        Line("        _ => throw Invalid(\"Unknown mapped payload: \" + alias)");
        Line("    };");
        Line("    public static string? StableName(string alias) => alias switch");
        Line("    {");
        foreach (var (wire, binding) in _messageBindings.Where(p => p.Value.Kind is not null))
            Line($"        \"{WireAlias(wire)}\" => \"{binding.Name}\",");
        Line("        _ => null");
        Line("    };");
        Line($"    public static string SerializePayload({ns}GamePayload value, System.Text.Json.JsonSerializerOptions options) => value switch");
        Line("    {");
        foreach (var (_, binding) in _messageBindings.Where(p => p.Value.Kind is not null))
            Line($"        {Type(Stable(binding.Name))} item => System.Text.Json.JsonSerializer.Serialize<{Type(Stable(binding.Name))}>(item, options),");
        Line("        _ => throw Invalid(\"Unknown stable payload\")");
        Line("    };");
        Line($"    public static {ns}GameMessageKind Kind({ns}GamePayload value) => value switch");
        Line("    {");
        foreach (string role in new[] { "Request", "Response", "Event" })
            Line($"        {ns}Game{role} => {ns}GameMessageKind.{role},");
        Line("        _ => throw Invalid(\"Unknown payload kind\")");
        Line("    };");
        Line($"    public static Google.Protobuf.WellKnownTypes.Any EncodePayload({ns}GamePayload value) => value switch");
        Line("    {");
        foreach (var (wire, binding) in _messageBindings.Where(p => p.Value.Kind is not null))
            Line($"        {Type(Stable(binding.Name))} item => new() {{ TypeUrl = \"type.ankama.com/{WireAlias(wire)}\", Value = ToWire(item).ToByteString() }},");
        Line("        _ => throw Invalid(\"Unknown stable payload\")");
        Line("    };");
        Line($"    public static byte[] Encode({ns}GameFrame frame)");
        Line("    {");
        Line("        var content = EncodePayload(frame.Content);");
        Line("        switch (Kind(frame.Content))");
        Line("        {");
        foreach (string role in new[] { "Request", "Response", "Event" })
        {
            var (_, branch) = EnvelopeField("Message", role.ToLowerInvariant());
            var (wire, content) = EnvelopeField(role, "content");
            string uid = role == "Event" ? "" : $", {Pascal(EnvelopeField(role, "uid").Field.Name)} = frame.Uid ?? throw Invalid(\"{role}.uid\")";
            Line($"            case {ns}GameMessageKind.{role}:");
            if (role == "Event") Line("                if (frame.Uid is not null) throw Invalid(\"Event cannot have uid\");");
            Line($"                return new {root.CSharpType} {{ {Pascal(branch.Name)} = new {wire.CSharpType} {{ {Pascal(content.Name)} = content{uid} }} }}.ToByteArray();");
        }
        Line("            default: throw Invalid(\"Message.kind\");");
        Line("        }");
        Line("    }");
        // Complete raw payload dispatch is independent of semantic mapping coverage.
        Line("    public static IMessage DecodeRawPayload(string alias, ReadOnlySpan<byte> payload) => alias switch");
        Line("    {");
        foreach (var wire in _messages.Values.Where(w => w.Descriptor.Options?.MapEntry != true && w.CSharpType.StartsWith(root.CSharpType[..root.CSharpType.LastIndexOf('.')], StringComparison.Ordinal)))
            Line($"        \"{WireAlias(wire)}\" => {wire.CSharpType}.Parser.ParseFrom(payload),");
        Line("        _ => throw Invalid(\"Unknown wire payload: \" + alias)");
        Line("    };");
    }

    private string WireAlias(WireMessage wire) => _mapping.WireAliases.GetValueOrDefault(wire.Descriptor.Name, wire.Descriptor.Name);
}
