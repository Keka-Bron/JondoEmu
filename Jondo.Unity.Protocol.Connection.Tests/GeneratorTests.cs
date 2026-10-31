using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Jondo.Unity.Protocol.Connection.Tests.Fixtures.Wire;
using Jondo.Unity.ProtocolGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Jondo.Unity.Protocol.Connection.Tests;

public sealed class GeneratorTests
{
    private const string Mapping = """
        {
          "stableNamespace": "Jondo.Unity.Protocol.Connection",
          "generatedNamespace": "Fixture.Generated",
          "className": "Bindings",
          "root": "FixtureMessage",
          "messages": {
            "a_root": { "name": "FixtureMessage", "oneofs": {
              "choice": { "member": "$self", "cases": {
                "item": { "type": "FixturePayload" }, "empty": { "type": "FixtureEmpty" }
              } }
            } },
            "a_payload": { "name": "FixturePayload", "fields": {
              "count": "Count", "ids": "Ids", "state": "State", "child": "Child", "required_child": "RequiredChild"
            }, "ignoredWireFields": ["decoy"] },
            "a_empty": { "name": "FixtureEmpty" }
          },
          "enums": { "mode": { "name": "FixtureMode", "values": { "ZERO": "Default", "ALLOWED": "Allowed" } } }
        }
        """;

    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator).Concat(new[] { typeof(FixtureMessage).Assembly.Location, typeof(ConnectionMessage).Assembly.Location,
            typeof(IMessage).Assembly.Location }).Distinct().Select(path => MetadataReference.CreateFromFile(path)).ToArray();

    private static CSharpCompilation Compilation() => CSharpCompilation.Create("Fixture_" + Guid.NewGuid().ToString("N"),
        references: References, options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            nullableContextOptions: NullableContextOptions.Enable));

    private static FileDescriptorSet Schema() => new() { File = { ConversionReflection.Descriptor.ToProto() } };

    private static Type Compile(string mapping = Mapping)
    {
        var compilation = Compilation();
        string source = BindingGenerator.Generate(Schema(), mapping, compilation);
        compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(source));
        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        output.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(output).GetType("Fixture.Generated.Bindings")!;
    }

    private static object Invoke(Type bindings, string method, params object[] args)
    {
        // Span methods cannot be invoked through reflection; use typed delegates for decode.
        try { return bindings.GetMethod(method)!.Invoke(null, args)!; }
        catch (TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw(); throw; }
    }

    private delegate FixtureMessage Decoder(ReadOnlySpan<byte> payload);
    private static FixtureMessage Decode(Type bindings, byte[] bytes)
        => bindings.GetMethod("Decode")!.CreateDelegate<Decoder>()(bytes);

    [Fact]
    public void Generated_code_executes_presence_repetition_enum_and_required_message_conversions()
    {
        var bindings = Compile();
        var wire = new a_root { Item = new a_payload { Count = 0, Ids = { -1L, long.MinValue, long.MaxValue },
            State = (mode)3, Decoy = "wire-only", RequiredChild = new a_empty() } };
        var stable = Assert.IsType<FixturePayload>(Decode(bindings, wire.ToByteArray()));
        Assert.Equal(0, stable.Count);
        Assert.Null(stable.Child);
        Assert.Equal(FixtureMode.Allowed, stable.State);
        Assert.Equal(new[] { -1L, long.MinValue, long.MaxValue }, stable.Ids);
        var encoded = a_root.Parser.ParseFrom((byte[])Invoke(bindings, "Encode", stable));
        Assert.True(encoded.Item.HasCount);
        Assert.Equal("", encoded.Item.Decoy);
        Assert.Equal(3, (int)encoded.Item.State);
        Assert.NotNull(encoded.Item.RequiredChild);
        wire.Item.ClearCount();
        Assert.Null(Assert.IsType<FixturePayload>(Decode(bindings, wire.ToByteArray())).Count);
        Assert.Equal(new byte[] { 0x42, 0 }, (byte[])Invoke(bindings, "Encode", new FixtureEmpty()));
    }

    [Fact]
    public void Rotated_aliases_fields_groups_and_tags_need_only_a_new_mapping()
    {
        string rotated = Mapping.Replace("a_root", "b_root").Replace("a_payload", "b_payload")
            .Replace("\"choice\"", "\"another_choice\"").Replace("\"item\"", "\"other_item\"")
            .Replace("\"empty\"", "\"other_empty\"");
        foreach (string field in new[] { "count", "ids", "state", "child", "decoy", "required_child" })
            rotated = rotated.Replace($"\"{field}\"", $"\"other_{field}\"");
        var bindings = Compile(rotated);
        var bytes = (byte[])Invoke(bindings, "Encode", new FixtureEmpty());
        Assert.Equal(new byte[] { 0x62, 0 }, bytes);
        var value = new FixturePayload(0, new[] { -10L }, FixtureMode.Allowed, new FixtureEmpty(), new FixtureEmpty());
        var payload = b_root.Parser.ParseFrom((byte[])Invoke(bindings, "Encode", value));
        Assert.True(payload.OtherItem.HasOtherCount);
        Assert.Equal(-10L, Assert.Single(payload.OtherItem.OtherIds));
        Assert.Equal(FixtureMode.Allowed, Assert.IsType<FixturePayload>(Decode(bindings, payload.ToByteArray())).State);
    }

    [Fact]
    public void Missing_cases_required_messages_and_unmapped_enum_values_are_errors()
    {
        var bindings = Compile();
        Assert.Throws<ConnectionConversionException>(() => Decode(bindings, []));
        Assert.Throws<ConnectionConversionException>(() => Decode(bindings, new a_root { Unmapped = new a_empty() }.ToByteArray()));
        var wire = new a_root { Item = new a_payload { RequiredChild = new a_empty(), State = (mode)7 } };
        Assert.Throws<ConnectionConversionException>(() => Decode(bindings, wire.ToByteArray()));
        wire.Item.State = (mode)99;
        Assert.Throws<ConnectionConversionException>(() => Decode(bindings, wire.ToByteArray()));
        wire.Item.State = (mode)0;
        wire.Item.RequiredChild = null;
        Assert.Throws<ConnectionConversionException>(() => Decode(bindings, wire.ToByteArray()));
        Assert.Throws<ConnectionConversionException>(() => Invoke(bindings, "Encode",
            new FixturePayload(null, [], (FixtureMode)99, null, new FixtureEmpty())));
    }

    [Theory]
    [InlineData("field")]
    [InlineData("type")]
    [InlineData("repetition")]
    [InlineData("presence")]
    [InlineData("cases")]
    [InlineData("enum")]
    [InlineData("ignored")]
    [InlineData("policy")]
    public void Invalid_bindings_fail_before_any_code_is_emitted(string mutation)
    {
        var mapping = JsonNode.Parse(Mapping)!;
        var schema = Schema();
        var payload = mapping["messages"]!["a_payload"]!;
        switch (mutation)
        {
            case "field": payload["fields"]!["not_a_field"] = "Count"; break;
            case "type": payload["fields"]!["state"] = "Count"; payload["fields"]!["count"] = "State"; break;
            case "repetition": payload["fields"]!["ids"] = "State"; payload["fields"]!["state"] = "Ids"; break;
            case "presence":
                var count = schema.File[0].MessageType.Single(m => m.Name == "a_payload").Field.Single(f => f.Name == "count");
                count.Proto3Optional = false;
                count.ClearOneofIndex();
                break;
            case "cases": mapping["messages"]!["a_root"]!["oneofs"]!["choice"]!["cases"]!.AsObject().Remove("empty"); break;
            case "enum": mapping["enums"]!["mode"]!["values"]!.AsObject().Remove("ALLOWED"); break;
            case "ignored": payload.AsObject().Remove("ignoredWireFields"); break;
            case "policy": payload["obsoletePolicy"] = true; break;
        }
        if (mutation == "policy")
            Assert.Throws<System.Text.Json.JsonException>(() => BindingGenerator.Generate(schema, mapping.ToJsonString(), Compilation()));
        else
            Assert.Throws<InvalidOperationException>(() => BindingGenerator.Generate(schema, mapping.ToJsonString(), Compilation()));
    }

    [Fact]
    public void Duplicate_mapping_keys_are_rejected()
        => Assert.Throws<InvalidOperationException>(() => BindingGenerator.Generate(Schema(),
            Mapping.Replace("\"count\": \"Count\"", "\"count\": \"Count\", \"count\": \"Count\""), Compilation()));
}
