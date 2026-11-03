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
            } },
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
    private delegate FixtureAdvanced AdvancedDecoder(ReadOnlySpan<byte> payload);
    private delegate IMessage RawDecoder(ReadOnlySpan<byte> payload);
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
        foreach (string field in new[] { "count", "ids", "state", "child", "required_child" })
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
    [InlineData("missing-member")]
    [InlineData("missing-oneof")]
    [InlineData("unknown-oneof")]
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
            case "missing-member": payload["fields"]!.AsObject().Remove("count"); break;
            case "missing-oneof": mapping["messages"]!["a_root"]!["oneofs"]!.AsObject().Remove("choice"); break;
            case "unknown-oneof": mapping["messages"]!["a_root"]!["oneofs"]!["not_a_group"] =
                mapping["messages"]!["a_root"]!["oneofs"]!["choice"]!.DeepClone(); break;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unmapped_fields_and_whole_oneofs_need_no_bindings_and_remain_available_raw(bool messageCase)
    {
        var bindings = Compile();
        var rawDecode = bindings.GetMethod("DecodeWire")!.CreateDelegate<RawDecoder>();
        var detail = new wire_only_detail { Data = ByteString.CopyFrom([0, 255]) };
        var payload = new a_payload { RequiredChild = new a_empty(), Decoy = "raw-only", Telemetry = 0,
            Opaque = detail.Data, Samples = { detail }, Attributes = { ["entry"] = detail },
            WireState = (wire_only_mode)99 };
        if (messageCase) payload.Detail = detail;
        else payload.Flag = false; // A present zero-valued branch must still be raw-only.
        byte[] bytes = new a_root { Item = payload }.ToByteArray().Concat(new byte[] { 0x98, 0x06, 1 }).ToArray();
        var raw = Assert.IsType<a_root>(rawDecode(bytes));
        Assert.Equal(bytes, raw.ToByteArray()); // Includes unknown tag 99 as well as declared unmapped fields.
        Assert.True(raw.Item.HasTelemetry);
        Assert.Equal(99, (int)raw.Item.WireState);
        Assert.Equal(detail, raw.Item.Attributes["entry"]);
        Assert.Equal(messageCase ? a_payload.WireOnlyChoiceOneofCase.Detail : a_payload.WireOnlyChoiceOneofCase.Flag,
            raw.Item.WireOnlyChoiceCase);

        var stable = Assert.IsType<FixturePayload>(Decode(bindings, bytes));
        Assert.Equal(FixtureMode.Default, stable.State);
        var encoded = a_root.Parser.ParseFrom((byte[])Invoke(bindings, "Encode", stable));
        Assert.Equal("", encoded.Item.Decoy);
        Assert.False(encoded.Item.HasTelemetry);
        Assert.Empty(encoded.Item.Opaque);
        Assert.Empty(encoded.Item.Samples);
        Assert.Empty(encoded.Item.Attributes);
        Assert.Equal(0, (int)encoded.Item.WireState);
        Assert.Equal(a_payload.WireOnlyChoiceOneofCase.None, encoded.Item.WireOnlyChoiceCase);
    }

    private const string AdvancedMapping = """
        {
          "stableNamespace": "Jondo.Unity.Protocol.Connection", "generatedNamespace": "Fixture.Generated",
          "className": "Bindings", "root": "FixtureAdvanced",
          "messages": {
            "advanced": { "name": "FixtureAdvanced",
              "fields": { "data": "Data", "title": "Title", "blob": "Blob", "state": "State",
                "children": "Children", "missing_default": "Defaulted" },
              "defaultOnAbsence": ["missing_default"], "ignoredStableMembers": ["Ignored"],
              "defaultOneofs": { "construction": "default_branch" },
              "oneofs": { "choice": { "member": "Choice", "noneType": "FixtureChoiceNone", "cases": {
                "number": { "type": "FixtureChoiceNumber", "member": "Value" },
                "empty": { "type": "FixtureChoiceEmpty", "member": "Value" }
              } } }
            },
            "defaults_payload": { "name": "FixtureDefaults", "fields": { "count": "Count", "text": "Text" } },
            "a_empty": { "name": "FixtureEmpty" }
          },
          "enums": { "mode": { "name": "FixtureMode", "values": { "ZERO": "Default", "ALLOWED": "Allowed" } } }
        }
        """;

    [Fact]
    public void Maps_bytes_optional_references_and_explicit_absence_policies_execute()
    {
        var bindings = Compile(AdvancedMapping);
        var decode = bindings.GetMethod("Decode")!.CreateDelegate<AdvancedDecoder>();
        var absent = decode([]);
        Assert.Empty(absent.Data);
        Assert.Null(absent.Title);
        Assert.Null(absent.Blob);
        Assert.Null(absent.State);
        Assert.IsType<FixtureChoiceNone>(absent.Choice);
        Assert.Equal(new FixtureDefaults(0, ""), absent.Defaulted);
        Assert.Equal("", absent.Ignored);

        var wire = new advanced { Title = "", Blob = ByteString.Empty, Data = ByteString.CopyFrom([0, 255]),
            State = (mode)0, Number = 0, Children = { [-42] = new defaults_payload { Count = 7, Text = "child" } } };
        var stable = decode(wire.ToByteArray());
        Assert.Equal("", stable.Title);
        Assert.Empty(stable.Blob!);
        Assert.Equal(new byte[] { 0, 255 }, stable.Data);
        Assert.Equal(FixtureMode.Default, stable.State);
        Assert.Equal(0, Assert.IsType<FixtureChoiceNumber>(stable.Choice).Value);
        Assert.Equal(new FixtureDefaults(7, "child"), stable.Children[-42]);
        var back = advanced.Parser.ParseFrom((byte[])Invoke(bindings, "Encode", stable));
        Assert.True(back.HasBlob);
        Assert.True(back.HasTitle);
        Assert.True(back.HasState);
        Assert.Equal(advanced.ChoiceOneofCase.Number, back.ChoiceCase);
        Assert.Equal(advanced.ConstructionOneofCase.DefaultBranch, back.ConstructionCase);
        Assert.Equal(7, back.Children[-42].Count);
        wire.Unsupported = false;
        Assert.Throws<ConnectionConversionException>(() => decode(wire.ToByteArray()));
        wire.ClearChoice();
        wire.State = (mode)7;
        Assert.Throws<ConnectionConversionException>(() => decode(wire.ToByteArray()));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("default")]
    [InlineData("ignored")]
    public void Invalid_absence_policies_are_rejected(string mutation)
    {
        var mapping = JsonNode.Parse(AdvancedMapping)!;
        var message = mapping["messages"]!["advanced"]!;
        if (mutation == "none") message["oneofs"]!["choice"]!["noneType"] = "FixtureChoiceNumber";
        if (mutation == "default") message["defaultOneofs"]!["construction"] = "number";
        if (mutation == "ignored") message["ignoredStableMembers"] = new JsonArray("Data");
        Assert.Throws<InvalidOperationException>(() => BindingGenerator.Generate(Schema(), mapping.ToJsonString(), Compilation()));
    }

    [Fact]
    public void Unmapped_whole_oneof_is_unset_without_an_explicit_default_policy()
    {
        var mapping = JsonNode.Parse(AdvancedMapping)!;
        mapping["messages"]!["advanced"]!.AsObject().Remove("defaultOneofs");
        var bindings = Compile(mapping.ToJsonString());
        var decode = bindings.GetMethod("Decode")!.CreateDelegate<AdvancedDecoder>();
        var stable = decode(new advanced { OtherBranch = "wire-only" }.ToByteArray());
        Assert.IsType<FixtureChoiceNone>(stable.Choice);
        var encoded = advanced.Parser.ParseFrom((byte[])Invoke(bindings, "Encode", stable));
        Assert.Equal(advanced.ConstructionOneofCase.None, encoded.ConstructionCase);
    }
}
