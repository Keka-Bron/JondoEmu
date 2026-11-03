using Google.Protobuf;
using Jondo.Unity.Protocol.Game.Wire;
using Xunit;

namespace Jondo.Unity.Protocol.Game.Tests;

public sealed class CodecTests
{
    private readonly GameCodec _codec = new();

    // Independent byte construction from the reviewed 3.6.10.10 envelope and payload
    // tags, not from generated wire classes or the codec under test.
    internal static byte[] Frame(int kindTag, string alias, byte[] payload, int uid = 0)
    {
        byte[] any = Write(o => { o.WriteTag(1, WireFormat.WireType.LengthDelimited); o.WriteString("type.ankama.com/" + alias);
            o.WriteTag(2, WireFormat.WireType.LengthDelimited); o.WriteBytes(ByteString.CopyFrom(payload)); });
        byte[] wrapper = Write(o => { o.WriteTag(1, WireFormat.WireType.LengthDelimited); o.WriteBytes(ByteString.CopyFrom(any));
            if (kindTag != 1) { o.WriteTag(2, WireFormat.WireType.Varint); o.WriteInt32(uid); } });
        return Write(o => { o.WriteTag(kindTag, WireFormat.WireType.LengthDelimited); o.WriteBytes(ByteString.CopyFrom(wrapper)); });
    }

    private static byte[] Write(Action<CodedOutputStream> write)
    {
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, true)) { write(output); output.Flush(); }
        return stream.ToArray();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    [InlineData(0)]
    public void Identification_fixture_preserves_signed_uid_and_semantic_fields(int uid)
    {
        byte[] body = [0x12, 0x04, (byte)'t', (byte)'e', (byte)'s', (byte)'t', 0x1a, 0x02, (byte)'e', (byte)'n'];
        byte[] frame = Frame(2, "kqz", body, uid);
        var decoded = _codec.Decode(frame);
        Assert.Equal(uid, decoded.Uid);
        Assert.Equal(new IdentificationRequest("test", "en"), Assert.IsType<IdentificationRequest>(decoded.Content));
        var envelope = _codec.ReadEnvelope(_codec.Encode(decoded));
        Assert.Equal(GameMessageKind.Request, envelope.Kind);
        Assert.Equal(uid, envelope.Uid);
        Assert.Equal("kqz", envelope.Alias);
        Assert.Equal(body, envelope.Content.Value.ToByteArray());
    }

    [Fact]
    public void Event_and_response_fixtures_keep_their_envelope_roles()
    {
        var pong = _codec.Decode(Frame(1, "kqy", [0x08, 0x01]));
        Assert.Equal(new PongEvent(true), pong.Content);
        Assert.Null(pong.Uid);
        Assert.Equal(GameMessageKind.Event, _codec.ReadEnvelope(_codec.Encode(pong)).Kind);
        var response = _codec.Decode(Frame(3, "jsq", [], -42));
        Assert.IsType<MapMovementConfirmedResponse>(response.Content);
        Assert.Equal(-42, response.Uid);
        Assert.Equal(GameMessageKind.Response, _codec.ReadEnvelope(_codec.Encode(response)).Kind);
        Assert.Throws<GameConversionException>(() => _codec.Encode(new(new PongEvent(true), 1)));
        Assert.Throws<GameConversionException>(() => _codec.Encode(new(new IdentificationRequest("", ""))));
    }

    [Fact]
    public void Raw_wire_retains_unmapped_and_unknown_fields_while_semantic_projection_omits_them()
    {
        byte[] body = [0x08, 0x7b, 0x12, 0x01, (byte)'x', 0x98, 0x06, 0x01];
        var wire = _codec.DecodeRawPayload("kqz", body);
        Assert.Equal(body, wire.ToByteArray());
        var stable = _codec.Decode(Frame(2, "kqz", body, 3));
        Assert.Equal(new IdentificationRequest("x", ""), stable.Content);
        Assert.Equal(new byte[] { 0x12, 0x01, (byte)'x' }, _codec.ReadEnvelope(_codec.Encode(stable)).Content.Value.ToByteArray());
        // Complete schema dispatch also includes types with no semantic binding,
        // including a C# keyword alias normalized only in the compiled schema.
        Assert.NotNull(_codec.DecodeRawPayload("int", []));
        Assert.Throws<GameConversionException>(() => GameBindings.DecodePayload("int", []));
    }

    [Fact]
    public void Bad_frames_unknown_types_and_wrong_envelope_kinds_fail_strict_conversion()
    {
        Assert.Throws<GameConversionException>(() => _codec.Decode([]));
        Assert.Throws<GameConversionException>(() => _codec.Decode(Frame(1, "kqz", [])));
        Assert.Throws<GameConversionException>(() => _codec.Decode(Frame(2, "unknown.payload", [], 1)));
        Assert.Throws<InvalidProtocolBufferException>(() => _codec.Decode(Frame(2, "kqz", [0x12, 0xff], 1)));
        Assert.Throws<GameConversionException>(() => _codec.Decode([0x12, 0x00]));
    }
}
