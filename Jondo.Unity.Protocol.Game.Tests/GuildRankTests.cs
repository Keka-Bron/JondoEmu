using Google.Protobuf;
using Jondo.Unity.Protocol.Game.Wire;
using Xunit;

namespace Jondo.Unity.Protocol.Game.Tests;

public sealed class GuildRankTests
{
    private readonly GameCodec _codec = new();

    [Fact]
    public void Empty_list_is_an_explicit_empty_snapshot()
    {
        Assert.Empty(Assert.IsType<GuildRanksEvent>(_codec.Decode(CodecTests.Frame(1, "jco", [])).Content).Ranks);
        AssertEncoded([], new([]));
    }

    [Theory]
    [InlineData(false, "1200")]
    [InlineData(true, "12041a002200")]
    public void Absent_and_present_empty_private_information_and_graphics_remain_distinct(bool present, string hex)
    {
        var expected = new RankInformation("", present ? new(false, []) : null, present ? new(0, 0) : null, 0);
        var decoded = Assert.Single(Assert.IsType<GuildRanksEvent>(_codec.Decode(CodecTests.Frame(1, "jco", Convert.FromHexString(hex))).Content).Ranks);
        Assert.Equal(expected.Id, decoded.Id);
        Assert.Equal(expected.OrderAndGfx, decoded.OrderAndGfx);
        Assert.Equal(present, decoded.PrivateInformation != null);
        if (present)
        {
            Assert.False(decoded.PrivateInformation!.Modifiable);
            Assert.Empty(decoded.PrivateInformation.Rights);
        }
        AssertEncoded(Convert.FromHexString(hex), new([expected]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Packed_and_unpacked_rights_preserve_order_duplicates_and_signed_int32_values(bool packed)
    {
        int[] rights = [257, 128, 257, 0, int.MaxValue, int.MinValue, -1];
        byte[] input = Payload(rights, packed);
        var decoded = Assert.Single(Assert.IsType<GuildRanksEvent>(_codec.Decode(CodecTests.Frame(1, "jco", input)).Content).Ranks);
        Assert.Equal(int.MaxValue, decoded.Id);
        Assert.Equal("Trésorier", decoded.Name);
        Assert.Equal(new RankInformationOrderAndGfx(91_001, 7), decoded.OrderAndGfx);
        Assert.True(decoded.PrivateInformation!.Modifiable);
        Assert.Equal(rights, decoded.PrivateInformation.Rights);
        AssertEncoded(Payload(rights, true), new([new("Trésorier", new(true, rights), new(91_001, 7), int.MaxValue)]));
    }

    [Fact]
    public void Historical_default_and_edited_rank_fixtures_match_semantic_payloads()
    {
        // Guild founding's four ranks, then the 192-byte rank-management refresh:
        // rename 1/2, replace 2's permissions, create 5 at display position 3.
        RankInformation[] defaults = [
            new("guild.rank.1.name", new(false, [1, 2, 5, 6, 7, 8, 13, 14, 15, 23, 24, 25, 26, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43]), new(116, 0), 1),
            new("guild.rank.2.name", new(true, [1, 2, 5, 6, 38, 7, 39, 8, 40, 41, 13, 14, 15, 23, 24, 25, 26]), new(115, 1), 2),
            new("guild.rank.3.name", new(true, []), new(114, 2), 3),
            new("guild.rank.4.name", new(false, []), new(117, 3), 4)];
        AssertEncoded(Convert.FromHexString(
            "123912116775696c642e72616e6b2e312e6e616d651a1e1a1c0102050607080d0e0f1718191a1d1e1f" +
            "202122232425262728292a2b220210742801123212116775696c642e72616e6b2e322e6e616d65" +
            "1a1508011a11010205062607270828290d0e0f1718191a2204107318012802121f12116775696c" +
            "642e72616e6b2e332e6e616d651a0208012204107218022803121d12116775696c642e72616e6b" +
            "2e342e6e616d651a002204107518032804"), new(defaults));
        RankInformation[] edited = [
            defaults[0] with { Name = "Tesorero" },
            defaults[1] with { Name = "Test rango", PrivateInformation = new(true, [1, 5, 6, 38, 7, 39, 8, 40, 41, 13, 14, 15, 23, 24, 25]) },
            defaults[2], new("Rango personalizado", new(true, []), new(102, 3), 5),
            defaults[3] with { OrderAndGfx = new(117, 4) }];
        AssertEncoded(Convert.FromHexString("123012085465736f7265726f1a1e1a1c0102050607080d0e0f1718191a1d1e1f202122232425262728292a2b2202107428011229120a546573742072616e676f1a1308011a0f0105062607270828290d0e0f1718192204107318012802121f12116775696c642e72616e6b2e332e6e616d651a02080122041072180228031221121352616e676f20706572736f6e616c697a61646f1a0208012204106618032805121d12116775696c642e72616e6b2e342e6e616d651a002204107518042804"), new(edited));
    }

    private void AssertEncoded(byte[] payload, GuildRanksEvent message)
    {
        var envelope = _codec.ReadEnvelope(_codec.Encode(new(message)));
        Assert.Equal(GameMessageKind.Event, envelope.Kind);
        Assert.Null(envelope.Uid);
        Assert.Equal("jco", envelope.Alias);
        Assert.Equal(payload, envelope.Content.Value.ToByteArray());
    }

    private static byte[] Payload(int[] rights, bool packed)
    {
        byte[] permissions = Write(writer =>
        {
            writer.WriteTag(1, WireFormat.WireType.Varint);
            writer.WriteBool(true);
            if (packed) Message(writer, 3, Write(values => { foreach (int id in rights) values.WriteInt32(id); }));
            else foreach (int id in rights)
            {
                writer.WriteTag(3, WireFormat.WireType.Varint);
                writer.WriteInt32(id);
            }
        });
        byte[] rank = Write(writer =>
        {
            writer.WriteTag(2, WireFormat.WireType.LengthDelimited);
            writer.WriteString("Trésorier");
            Message(writer, 3, permissions);
            Message(writer, 4, Write(gfx =>
            {
                gfx.WriteTag(2, WireFormat.WireType.Varint); gfx.WriteInt32(91_001);
                gfx.WriteTag(3, WireFormat.WireType.Varint); gfx.WriteInt32(7);
            }));
            writer.WriteTag(5, WireFormat.WireType.Varint); writer.WriteInt32(int.MaxValue);
        });
        return Write(writer => Message(writer, 2, rank));
    }

    private static void Message(CodedOutputStream writer, int tag, byte[] body)
    {
        writer.WriteTag(tag, WireFormat.WireType.LengthDelimited);
        writer.WriteBytes(ByteString.CopyFrom(body));
    }

    private static byte[] Write(Action<CodedOutputStream> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new CodedOutputStream(stream, leaveOpen: true)) write(writer);
        return stream.ToArray();
    }
}
