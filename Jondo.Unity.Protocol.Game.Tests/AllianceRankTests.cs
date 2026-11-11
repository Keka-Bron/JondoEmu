using Google.Protobuf;
using Jondo.Unity.Protocol.Game.Wire;
using Xunit;

namespace Jondo.Unity.Protocol.Game.Tests;

public sealed class AllianceRankTests
{
    private readonly GameCodec _codec = new();

    [Fact]
    public void Empty_list_is_an_explicit_empty_snapshot() => AssertFixture([], new([]));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Private_information_and_graphics_keep_independent_absent_and_present_empty_states(bool permissions, bool graphics)
    {
        byte[] rank = [.. permissions ? Msg(3, []) : Array.Empty<byte>(), .. graphics ? Msg(4, []) : Array.Empty<byte>()];
        AssertFixture(Msg(1, rank), new([new("", permissions ? new(false, []) : null, graphics ? new(0, 0) : null, 0)]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Packed_and_unpacked_rights_keep_order_duplicates_unknown_ids_and_full_signed_int32_values(bool packed)
    {
        int[] rights = [257, 128, 257, 0, int.MaxValue, int.MinValue, -1];
        var expected = new AllianceRanksEvent([new("Gardien", new(true, rights), new(91_001, 7), int.MinValue)]);
        byte[] Payload(bool usePacked)
        {
            byte[] values = usePacked ? Msg(3, Write(writer => { foreach (int right in rights) writer.WriteInt32(right); }))
                : rights.SelectMany(right => Var(3, right)).ToArray();
            return Msg(1, [.. Text(2, "Gardien"), .. Msg(3, [.. Var(1, 1), .. values]),
                .. Msg(4, [.. Var(2, 91_001), .. Var(3, 7)]), .. Var(5, int.MinValue)]);
        }
        AssertSnapshot(expected, _codec.Decode(CodecTests.Frame(1, "mbm", Payload(packed))));
        AssertFixture(Payload(true), expected);
    }

    [Fact]
    public void Historical_alliance_custom_rank_payload_matches_the_semantic_snapshot()
    {
        // «E X A M P L E»: four custom names, unequal rights and icons, empty
        // newcomer permissions and a false Modifiable value still retain presence.
        RankInformation[] expected = [
            new("Rank Number 01", new(false, [1, 2, 3, 4, 6, 7, 8, 9, 10, 11, 12, 13, 15, 16, 17, 18, 19, 21]), new(116, 0), 1),
            new("Rank Number 02", new(true, [1, 2, 3, 4, 6, 7, 8, 9, 10, 12, 13, 15, 16, 17, 18, 21]), new(23, 1), 2),
            new("Rank Number 003", new(true, [18, 9, 10, 12]), new(45, 2), 3),
            new("Rank No 4", new(false, []), new(117, 3), 4)];
        AssertFixture(Convert.FromHexString("0a2c120e52616e6b204e756d6265722030311a141a1201020304060708090a0b0c0d0f10111213152202107428010a2e120e52616e6b204e756d6265722030321a1408011a1001020304060708090a0c0d0f1011121522041017180128020a23120f52616e6b204e756d626572203030331a0808011a0412090a0c2204102d180228030a15120952616e6b204e6f20341a002204107518032804"), new(expected));
    }

    private void AssertFixture(byte[] payload, AllianceRanksEvent expected)
    {
        AssertSnapshot(expected, _codec.Decode(CodecTests.Frame(1, "mbm", payload)));
        var envelope = _codec.ReadEnvelope(_codec.Encode(new(expected)));
        Assert.Equal(GameMessageKind.Event, envelope.Kind);
        Assert.Null(envelope.Uid);
        Assert.Equal("mbm", envelope.Alias);
        Assert.Equal(payload, envelope.Content.Value.ToByteArray());
    }
    private static void AssertSnapshot(AllianceRanksEvent expected, GameFrame frame)
    {
        Assert.Null(frame.Uid);
        var actual = Assert.IsType<AllianceRanksEvent>(frame.Content);
        Assert.Equal(expected.Ranks.Count, actual.Ranks.Count);
        for (int i = 0; i < expected.Ranks.Count; i++)
        {
            var rank = actual.Ranks[i];
            var wanted = expected.Ranks[i];
            Assert.Equal(wanted.Id, rank.Id);
            Assert.Equal(wanted.Name, rank.Name);
            Assert.Equal(wanted.OrderAndGfx, rank.OrderAndGfx);
            Assert.Equal(wanted.PrivateInformation?.Modifiable, rank.PrivateInformation?.Modifiable);
            Assert.Equal(wanted.PrivateInformation?.Rights, rank.PrivateInformation?.Rights);
        }
    }
    private static byte[] Var(int field, int value) => Write(writer => { writer.WriteTag(field, WireFormat.WireType.Varint); writer.WriteInt32(value); });
    private static byte[] Text(int field, string value) => Msg(field, System.Text.Encoding.UTF8.GetBytes(value));
    private static byte[] Msg(int field, byte[] value) => Write(writer => { writer.WriteTag(field, WireFormat.WireType.LengthDelimited); writer.WriteBytes(ByteString.CopyFrom(value)); });
    private static byte[] Write(Action<CodedOutputStream> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new CodedOutputStream(stream, leaveOpen: true)) write(writer);
        return stream.ToArray();
    }
}
