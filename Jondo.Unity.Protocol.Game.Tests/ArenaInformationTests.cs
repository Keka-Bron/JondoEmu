using Google.Protobuf;
using Jondo.Unity.Protocol.Game.Wire;
using Xunit;

namespace Jondo.Unity.Protocol.Game.Tests;

/// <summary>Independent August fixtures for arena fields and nullable ranking presence.</summary>
public sealed class ArenaInformationTests
{
    private readonly GameCodec _codec = new();
    private const string Expiry = "2026-11-11T12:39:56.789Z";

    [Theory]
    [InlineData(0, ArenaType.OneVsOne)]
    [InlineData(1, ArenaType.TwoVsTwo)]
    [InlineData(2, ArenaType.ThreeVsThree)]
    [InlineData(3, ArenaType.Event)]
    public void Populated_fields_bind_distinct_rating_league_and_counter_values(int mode, ArenaType expectedMode)
    {
        byte[] ranking = [.. Var(1, 7), .. Var(2, 645), .. Var(4, 99)];
        byte[] rank = [.. mode == 0 ? Array.Empty<byte>() : Var(1, mode),
            .. Var(2, 1555), .. Var(3, 9), .. Var(4, 4), .. Var(5, 17), .. Var(6, 31),
            .. Var(7, 2), .. Msg(8, ranking), .. Var(10, 5)];
        byte[] body = [.. Text(2, Expiry), .. Msg(3, rank)];
        var expected = new ArenaUpdatePlayerInformationEvent(Expiry,
            [new(expectedMode, 1555, 9, 4, 17, 31, 2, new(7, 645, 99), 5)]);
        AssertFixture(body, expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Absent_ranking_and_present_empty_ranking_remain_distinct(bool present)
    {
        byte[] body = [.. Text(2, Expiry), .. Msg(3, present ? Msg(8, []) : [])];
        AssertFixture(body, new(Expiry, [new(ArenaType.OneVsOne, 0, 0, 0, 0, 0, 0,
            present ? new(0, 0, 0) : null, 0)]));
    }

    [Fact]
    public void Unplaced_signed_league_sentinels_and_empty_snapshot_are_preserved()
    {
        byte[] rank = [.. Var(3, -1), .. Var(4, 5), .. Msg(8, [.. Var(1, -1), .. Var(2, 1000)])];
        AssertFixture([.. Text(2, Expiry), .. Msg(3, rank)],
            new(Expiry, [new(ArenaType.OneVsOne, 0, -1, 5, 0, 0, 0, new(-1, 1000, 0), 0)]));
        AssertFixture(Text(2, Expiry), new(Expiry, []));
    }

    private void AssertFixture(byte[] body, ArenaUpdatePlayerInformationEvent expected)
    {
        var decoded = _codec.Decode(CodecTests.Frame(1, "lty", body));
        Assert.Null(decoded.Uid);
        var actual = Assert.IsType<ArenaUpdatePlayerInformationEvent>(decoded.Content);
        Assert.Equal(expected.BanEndDate, actual.BanEndDate);
        Assert.Equal(expected.ArenaRanks.ToArray(), actual.ArenaRanks.ToArray());
        var envelope = _codec.ReadEnvelope(_codec.Encode(new(expected)));
        Assert.Equal(GameMessageKind.Event, envelope.Kind);
        Assert.Null(envelope.Uid);
        Assert.Equal("lty", envelope.Alias);
        Assert.Equal(body, envelope.Content.Value.ToByteArray());
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

    private static byte[] Text(int field, string value) => Msg(field, System.Text.Encoding.UTF8.GetBytes(value));
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
