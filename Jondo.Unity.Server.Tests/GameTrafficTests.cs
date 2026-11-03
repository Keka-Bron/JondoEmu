using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Jondo.Protocol;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Protocol.Game.Wire.Messages;
using Jondo.Unity.Server.Diagnostics;
using Jondo.Unity.Server.UI;
using Xunit;

namespace Jondo.Unity.Server.Tests;

[CollectionDefinition("Game traffic console", DisableParallelization = true)]
public sealed class GameTrafficConsoleCollection;

[Collection("Game traffic console")]
public sealed class GameTrafficTests
{
    [Fact]
    public void Mapped_live_output_keeps_uid_fields_enums_and_nested_cases()
    {
        var codec = new GameCodec();
        var request = codec.Encode(new(new ChatChannelMessageRequest("hello\nworld", ChatChannel.Guild), -4));
        Assert.True(GameTrafficFormatter.TryFormat(request, out var name, out var fields));
        Assert.Equal("ChatChannelMessageRequest", name);
        Assert.Contains("uid=-4", fields);
        Assert.Contains("\"Channel\":\"Guild\"", fields);
        Assert.Contains("hello\\nworld", fields);
        Assert.DoesNotContain('\n', fields);

        var item = new ObjectItem(1, [new ObjectEffect(123, new ObjectEffectCaseValueInt(42))], 2, 3, []);
        var inventory = codec.Encode(new(new InventoryContentEvent(10, [new ObjectItemInventory(0, false, item)])));
        Assert.True(GameTrafficFormatter.TryFormat(inventory, out _, out fields));
        Assert.Contains("\"$type\":\"ValueInt\"", fields);
        Assert.Contains("\"Value\":42", fields);
        var selection = codec.Encode(new(new CharacterSelectionEventCaseError(new CharacterSelectionError())));
        Assert.True(GameTrafficFormatter.TryFormat(selection, out name, out fields));
        Assert.Equal("CharacterSelectionEvent", name);
        Assert.Contains("\"$type\":\"Error\"", fields);
    }

    [Fact]
    public async Task Live_logging_redacts_a_copy_and_leaves_the_received_frame_unchanged()
    {
        string token = "0123456789abcdef0123456789abcdef";
        byte[] frame = new GameCodec().Encode(new(new IdentificationRequest(token, "en"), 4));
        var (returned, log) = await ReadWithLog(frame);
        Assert.Equal(frame, returned);
        Assert.Equal(token, Assert.IsType<IdentificationRequest>(new GameCodec().Decode(returned).Content).TicketKey);
        Assert.DoesNotContain(token, log);
        Assert.Contains(new string('x', 32), log);
        Assert.Contains("IdentificationRequest", log);
        Assert.Contains("TicketKey", log);
        Assert.Contains("uid=4", log);
    }

    [Theory]
    [InlineData("unknown.payload", false)]
    [InlineData("kqz", true)]
    public async Task Mapping_failure_falls_back_to_raw_fields_without_affecting_read(string alias, bool malformed)
    {
        byte[] body = malformed ? [0x08, 0x7b, 0x12, 0xff] : [0x08, 0x7b];
        var wire = new hea { Fllm = new hdy { Fllh = -1, Fllg = new Any
            { TypeUrl = "type.ankama.com/" + alias, Value = ByteString.CopyFrom(body) } } };
        byte[] frame = wire.ToByteArray();
        Assert.False(GameTrafficFormatter.TryFormat(frame, out var name, out _));
        Assert.Equal(malformed ? "IdentificationRequest" : "", name);
        var (returned, log) = await ReadWithLog(frame);
        Assert.Equal(frame, returned);
        Assert.Contains("1: 123", log);
        Assert.Contains(alias, log);
    }

    [Fact]
    public void Live_fields_are_bounded_and_malformed_envelopes_are_nonthrowing()
    {
        byte[] frame = new GameCodec().Encode(new(new ChatChannelMessageRequest(new string('z', 10000), ChatChannel.General), 1));
        Assert.True(GameTrafficFormatter.TryFormat(frame, out _, out var fields));
        Assert.Equal(GameTrafficFormatter.MaxLiveFields + 1, fields.Length);
        Assert.EndsWith("…", fields);
        Assert.False(GameTrafficFormatter.TryFormat([0xff], out _, out _));
        var row = Assert.Single(ServerLog.Format("", "  42 [client>server] foo.bar (MappedRequest) uid=-1 {\"Count\":0} 123 B"));
        Assert.Equal("foo.bar", row.Opcode);
    }

    private static async Task<(byte[] Frame, string Log)> ReadWithLog(byte[] frame)
    {
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, true)) { output.WriteBytes(ByteString.CopyFrom(frame)); output.Flush(); }
        stream.Position = 0;
        var previous = Console.Out;
        using var log = new StringWriter();
        Console.SetOut(log);
        try { return (await NetworkMessage.ReadFrameAsync(stream), log.ToString()); }
        finally { Console.SetOut(previous); }
    }
}
