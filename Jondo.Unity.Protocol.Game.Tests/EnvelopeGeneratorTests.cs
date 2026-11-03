using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using GameFixture.Wire;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.ProtocolGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Jondo.Unity.Protocol.Game.Tests;

public sealed class EnvelopeGeneratorTests
{
    private const string Mapping = """
        {
          "stableNamespace": "Jondo.Unity.Protocol.Game", "generatedNamespace": "Jondo.Unity.Protocol.Game.Wire",
          "className": "SyntheticBindings", "conversionException": "GameConversionException",
          "messages": { "source_payload": { "name": "SyntheticRequest", "kind": "request", "fields": { "text": "Text" } } },
          "gameEnvelope": {
            "Message": { "alias": "source_root", "fields": { "notice": "event", "ask": "request", "reply": "response" } },
            "Event": { "alias": "source_event", "fields": { "item": "content" } },
            "Request": { "alias": "source_request", "fields": { "item": "content", "id": "uid" } },
            "Response": { "alias": "source_response", "fields": { "item": "content", "id": "uid" } }
          }
        }
        """;

    private static CSharpCompilation Compilation() => CSharpCompilation.Create("GameFixture_" + Guid.NewGuid().ToString("N"),
        references: ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat([typeof(SyntheticRequest).Assembly.Location, typeof(GameFrame).Assembly.Location, typeof(GameCodec).Assembly.Location,
                typeof(IMessage).Assembly.Location]).Distinct().Select(p => MetadataReference.CreateFromFile(p)),
        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    private static FileDescriptorSet Schema() => new() { File = { Any.Descriptor.File.ToProto(), GameFixtureReflection.Descriptor.ToProto() } };
    private delegate GameFrame Decoder(ReadOnlySpan<byte> data);
    private delegate IMessage RawPayloadDecoder(string alias, ReadOnlySpan<byte> data);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Envelope_dispatch_is_generated_from_rotatable_bindings(bool rotated)
    {
        string mapping = Mapping;
        if (rotated)
        {
            mapping = mapping.Replace("source_", "rotated_");
            foreach (string field in new[] { "notice", "ask", "reply", "item", "id", "text" })
                mapping = mapping.Replace($"\"{field}\"", $"\"other_{field}\"");
        }
        var compilation = Compilation();
        string generated = BindingGenerator.Generate(Schema(), mapping, compilation);
        compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(generated));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        stream.Position = 0;
        var type = AssemblyLoadContext.Default.LoadFromStream(stream).GetType("Jondo.Unity.Protocol.Game.Wire.SyntheticBindings")!;
        var decode = type.GetMethod("Decode")!.CreateDelegate<Decoder>();
        var decodeRaw = type.GetMethod("DecodeRawPayload")!.CreateDelegate<RawPayloadDecoder>();
        byte[] encoded = (byte[])type.GetMethod("Encode")!.Invoke(null, [new GameFrame(new SyntheticRequest("hi"), -123)])!;
        Assert.Equal(new GameFrame(new SyntheticRequest("hi"), -123), decode(encoded));
        if (rotated)
        {
            var wire = rotated_root.Parser.ParseFrom(encoded);
            Assert.Equal(-123, wire.OtherAsk.OtherId);
            Assert.Equal("type.ankama.com/rotated_payload", wire.OtherAsk.OtherItem.TypeUrl);
            Assert.Equal(new byte[] { 0x2a, 0x02, (byte)'h', (byte)'i' }, wire.OtherAsk.OtherItem.Value.ToByteArray());
            var payload = new rotated_payload { OtherText = "hi", OtherDiagnostic = "raw-only", OtherReason = 0 };
            wire.OtherAsk.OtherItem.Value = payload.ToByteString();
            Assert.Equal(payload, Assert.IsType<rotated_payload>(decodeRaw("rotated_payload", payload.ToByteArray())));
            Assert.Equal(new GameFrame(new SyntheticRequest("hi"), -123), decode(wire.ToByteArray()));
            Assert.Equal(encoded, (byte[])type.GetMethod("Encode")!.Invoke(null, [decode(wire.ToByteArray())])!);
        }
        else
        {
            var wire = source_root.Parser.ParseFrom(encoded);
            Assert.Equal(-123, wire.Ask.Id);
            Assert.Equal(new byte[] { 0x5a, 0x02, (byte)'h', (byte)'i' }, wire.Ask.Item.Value.ToByteArray());
            var payload = new source_payload { Text = "hi", Diagnostic = "raw-only", Reason = 0 };
            wire.Ask.Item.Value = payload.ToByteString();
            Assert.Equal(payload, Assert.IsType<source_payload>(decodeRaw("source_payload", payload.ToByteArray())));
            Assert.Equal(new GameFrame(new SyntheticRequest("hi"), -123), decode(wire.ToByteArray()));
            Assert.Equal(encoded, (byte[])type.GetMethod("Encode")!.Invoke(null, [decode(wire.ToByteArray())])!);
        }
    }

    [Theory]
    [InlineData("uid")]
    [InlineData("kind")]
    [InlineData("branch")]
    public void Invalid_envelope_bindings_fail_before_emission(string mutation)
    {
        var mapping = JsonNode.Parse(Mapping)!;
        if (mutation == "uid") mapping["gameEnvelope"]!["Request"]!["fields"]!["id"] = "unrecognized";
        if (mutation == "kind") mapping["messages"]!["source_payload"]!["kind"] = "event";
        if (mutation == "branch") mapping["gameEnvelope"]!["Message"]!["fields"]!["ask"] = "event";
        Assert.Throws<InvalidOperationException>(() => BindingGenerator.Generate(Schema(), mapping.ToJsonString(), Compilation()));
    }
}
