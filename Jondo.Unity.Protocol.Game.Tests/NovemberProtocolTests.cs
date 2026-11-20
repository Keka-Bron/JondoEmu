#if JONDO_PROTOCOL_3_8_1_2
using Google.Protobuf;
using Jondo.Unity.Protocol.Game.Wire;
using Xunit;

namespace Jondo.Unity.Protocol.Game.Tests;

public sealed class NovemberProtocolTests
{
    private readonly GameCodec _codec = new();

    [Theory]
    [InlineData(0)]
    [InlineData(-42)]
    public void Identification_uses_the_new_request_envelope_and_payload_order(int uid)
    {
        Assert.Equal("3.8.1.2", GameCodec.JondoVersion);
        byte[] payload = Convert.FromHexString("0A02656E120474657374");
        byte[] wire = Frame(1, 2, "krv", payload, uidTag: 1, uid: uid);
        var expected = new GameFrame(new IdentificationRequest("test", "en"), uid);
        Assert.Equal(expected, _codec.Decode(wire));
        Assert.Equal(wire, _codec.Encode(expected));
        Assert.Throws<GameConversionException>(() => _codec.Decode(Frame(2, 1, "krv", payload)));
    }

    [Fact]
    public void Events_have_no_uid_and_responses_use_the_distinct_response_envelope()
    {
        var reply = new GameFrame(new MapMovementConfirmedResponse(), -7);
        byte[] response = Frame(3, 1, "jry", [], uidTag: 2, uid: -7);
        Assert.Equal(reply, _codec.Decode(response));
        Assert.Equal(response, _codec.Encode(reply));

        var settings = new GameFrame(new ServerSettingsEvent(30, ServerType.Classical, false, false, 0, "en"));
        var encoded = _codec.Encode(settings);
        Assert.Equal(0x12, encoded[0]); // Message.event is now tag 2.
        var envelope = _codec.ReadEnvelope(encoded);
        Assert.Equal("hot", envelope.Alias);
        Assert.Null(envelope.Uid);
        Assert.Equal(settings, _codec.Decode(encoded));
    }

    private static byte[] Frame(int rootTag, int anyTag, string alias, byte[] body, int? uidTag = null, int uid = 0)
    {
        byte[] any = Write(o =>
        {
            o.WriteTag(1, WireFormat.WireType.LengthDelimited);
            o.WriteString("type.ankama.com/" + alias);
            if (body.Length > 0) WriteMessage(o, 2, body);
        });
        byte[] wrapper = Write(o =>
        {
            if (uidTag < anyTag) WriteUid(o);
            WriteMessage(o, anyTag, any);
            if (uidTag > anyTag) WriteUid(o);
        });
        return Write(o => WriteMessage(o, rootTag, wrapper));

        void WriteUid(CodedOutputStream o)
        {
            if (uid == 0) return;
            o.WriteTag(uidTag!.Value, WireFormat.WireType.Varint);
            o.WriteInt32(uid);
        }
    }

    private static void WriteMessage(CodedOutputStream output, int tag, byte[] bytes)
    {
        output.WriteTag(tag, WireFormat.WireType.LengthDelimited);
        output.WriteBytes(ByteString.CopyFrom(bytes));
    }

    private static byte[] Write(Action<CodedOutputStream> write)
    {
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, true))
        {
            write(output);
            output.Flush();
        }
        return stream.ToArray();
    }
}
#endif
