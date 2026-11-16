using Google.Protobuf;
using Jondo.Unity.Protocol.Game.Wire;
using Xunit;

namespace Jondo.Unity.Protocol.Game.Tests;

public sealed class StartupReplyContractTests
{
    private readonly GameCodec _codec = new();

    [Theory]
    [InlineData(5, "0805")]
    [InlineData(4, "0804")]
    [InlineData(3, "0803")]
    [InlineData(0, "")]
    public void Contributions_left_matches_the_independent_populated_and_empty_payloads(int left, string hex)
    {
        byte[] body = Convert.FromHexString(hex);
        Assert.Equal(new GuildContributionLeftEvent(left), _codec.Decode(CodecTests.Frame(1, "jla", body)).Content);
        AssertBody("jla", body, new GuildContributionLeftEvent(left));
    }

    [Fact]
    public void Average_price_table_binds_tag_two_entries_with_distinct_ids_and_int64_prices()
    {
        ObjectAveragePrice[] entries = [new(91_001, 8_000_000_003), new(91_002, long.MaxValue), new(0, 73)];
        // Independent protobuf construction using the reviewed nested tags, not generated wire types.
        byte[] body = Write(output =>
        {
            foreach (var entry in entries)
            {
                byte[] row = Write(inner =>
                {
                    if (entry.ObjectGid != 0) { inner.WriteTag(1, WireFormat.WireType.Varint); inner.WriteInt32(entry.ObjectGid); }
                    inner.WriteTag(2, WireFormat.WireType.Varint);
                    inner.WriteInt64(entry.AveragePrice);
                });
                output.WriteTag(2, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(ByteString.CopyFrom(row));
            }
        });
        var frame = _codec.Decode(CodecTests.Frame(1, "ivi", body));
        Assert.Null(frame.Uid);
        Assert.Equal(entries, Assert.IsType<ObjectAveragePricesEvent>(frame.Content).ObjectsAveragePrices);
        AssertBody("ivi", body, new ObjectAveragePricesEvent(entries));
    }

    [Fact]
    public void Empty_table_is_distinct_from_a_present_empty_price_entry()
    {
        Assert.Empty(Assert.IsType<ObjectAveragePricesEvent>(_codec.Decode(CodecTests.Frame(1, "ivi", [])).Content).ObjectsAveragePrices);
        AssertBody("ivi", [], new ObjectAveragePricesEvent([]));
        byte[] body = [0x12, 0x00];
        var prices = Assert.IsType<ObjectAveragePricesEvent>(_codec.Decode(CodecTests.Frame(1, "ivi", body)).Content);
        Assert.Equal(new ObjectAveragePrice(0, 0), Assert.Single(prices.ObjectsAveragePrices));
        AssertBody("ivi", body, new ObjectAveragePricesEvent([new(0, 0)]));
    }

    private void AssertBody(string alias, byte[] body, GameEvent message)
    {
        var envelope = _codec.ReadEnvelope(_codec.Encode(new(message)));
        Assert.Equal(GameMessageKind.Event, envelope.Kind);
        Assert.Null(envelope.Uid);
        Assert.Equal(alias, envelope.Alias);
        Assert.Equal(body, envelope.Content.Value.ToByteArray());
    }

    private static byte[] Write(Action<CodedOutputStream> action)
    {
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, leaveOpen: true)) action(output);
        return stream.ToArray();
    }
}
