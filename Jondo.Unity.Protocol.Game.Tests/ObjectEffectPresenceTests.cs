using Google.Protobuf;
using Jondo.Unity.Protocol.Game.Wire;
using Xunit;

namespace Jondo.Unity.Protocol.Game.Tests;

public sealed class ObjectEffectPresenceTests
{
    [Fact]
    public void Unset_effect_integer_zero_and_present_empty_dice_have_distinct_semantic_and_wire_presence()
    {
        var codec = new GameCodec();
        const int action = 91_001, alterationId = 81_001;
        (ObjectEffectCase Value, byte[] Fields)[] cases = [
            (new ObjectEffectCaseNone(), []),
            (new ObjectEffectCaseValueInt(0), Var(4, 0)),
            (new ObjectEffectCaseValueDice(new(0, 0, 0)), Msg(6, []))];
        var serialized = new HashSet<string>();
        foreach (var (value, fields) in cases)
        {
            // Independent August fixture: effect action=11, value int=4/dice=6;
            // alteration id=2/effects=3/expiration=4 (infinite=1), snapshot list=1.
            byte[] effect = [.. fields, .. Var(11, action)];
            byte[] body = Msg(1, [.. Var(2, alterationId), .. Msg(3, effect), .. Var(4, 1)]);
            var decoded = codec.Decode(CodecTests.Frame(1, "lzp", body));
            Assert.Null(decoded.Uid);
            var actual = Assert.Single(Assert.Single(Assert.IsType<AlterationsEvent>(decoded.Content).Alterations).Effects);
            Assert.Equal(new ObjectEffect(action, value), actual);

            var message = new AlterationsEvent([new(0, alterationId, [new ObjectEffect(action, value)],
                AlterationExpirationType.Infinite, 0)]);
            var envelope = codec.ReadEnvelope(codec.Encode(new(message)));
            Assert.Equal(GameMessageKind.Event, envelope.Kind);
            Assert.Equal(body, envelope.Content.Value.ToByteArray());
            Assert.True(serialized.Add(Convert.ToHexString(envelope.Content.Value.Span)));
        }
    }

    private static byte[] Var(int field, int value)
    {
        using var stream = new MemoryStream();
        using (var writer = new CodedOutputStream(stream, leaveOpen: true))
        {
            writer.WriteTag(field, WireFormat.WireType.Varint);
            writer.WriteInt32(value);
        }
        return stream.ToArray();
    }

    private static byte[] Msg(int field, byte[] value)
    {
        using var stream = new MemoryStream();
        using (var writer = new CodedOutputStream(stream, leaveOpen: true))
        {
            writer.WriteTag(field, WireFormat.WireType.LengthDelimited);
            writer.WriteBytes(ByteString.CopyFrom(value));
        }
        return stream.ToArray();
    }
}
