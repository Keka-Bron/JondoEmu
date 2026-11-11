using Google.Protobuf;
using Jondo.Unity.Protocol.Game.Wire;
using Xunit;

namespace Jondo.Unity.Protocol.Game.Tests;

public sealed class GuildStateTests
{
    private readonly GameCodec _codec = new();
    private static readonly GuildInformation HistoricalGuild = new(42043, "Jondo", 1,
        new GuildSocialInformation(new SocialEmblem(165, 8, 16744448, 9476018)));

    [Fact]
    public void Historical_membership_preserves_both_omitted_and_populated_context_values()
    {
        // World-entry capture after two contributions. The reviewed placeholder
        // retains the historical 1791 without assigning it a gameplay meaning.
        byte[] original = Convert.FromHexString("0a200a111a0f08a5011008188080fe0728b2afc20410bbc8021a054a6f6e646f20011001181420ff0d");
        AssertFixture("jhe", original, new GuildMembershipEvent(HistoricalGuild, 1, 20, 1791));
        AssertFixture("jhe", original[..^3], new GuildMembershipEvent(HistoricalGuild, 1, 20, 0));
    }

    [Fact]
    public void Historical_founding_and_contributed_headers_match_independent_payloads()
    {
        const string created = "2026-08-12T20:53:02.187515342Z";
        AssertFixture("jhh", Convert.FromHexString("0a1e323032362d30382d31325432303a35333a30322e3138373531353334325a180148325001"),
            new GuildGeneralInformationEvent(created, null, 1, 0, 0, 0, 0, 50, 1));
        AssertFixture("jhh", Convert.FromHexString("0a1e323032362d30382d31325432303a35333a30322e3138373531353334325a1801380a48325002"),
            new GuildGeneralInformationEvent(created, null, 1, 0, 0, 0, 10, 50, 2));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(83_003)]
    public void Hall_presence_nonzero_score_and_int64_header_values_bind_independently(int? hallId)
    {
        byte[] body = [.. Text(1, "2026-11-11T12:34:56.789Z"), .. hallId.HasValue ? Var(2, hallId.Value) : Array.Empty<byte>(),
            .. Var(3, 4), .. Var(4, 73), .. Var(5, 150), .. Var(6, long.MaxValue), .. Var(7, 8_000_000_003), .. Var(9, 210), .. Var(10, 17)];
        AssertFixture("jhh", body, new GuildGeneralInformationEvent("2026-11-11T12:34:56.789Z", hallId, 4, 73, 150,
            long.MaxValue, 8_000_000_003, 210, 17));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Guild_social_and_emblem_absence_are_distinct_from_present_empty_blocks(int depth)
    {
        GuildInformation? guild = depth == 0 ? null : new(0, "", 0,
            depth == 1 ? null : new GuildSocialInformation(depth == 2 ? null : new SocialEmblem(0, 0, 0, 0)));
        byte[] body = depth == 0 ? [] : Msg(1, depth == 1 ? [] : Msg(1, depth == 2 ? [] : Msg(3, [])));
        AssertFixture("jhe", body, new GuildMembershipEvent(guild, 0, 0, 0));
    }

    [Fact]
    public void Distinct_emblem_values_full_int32_scalars_and_zero_header_are_supported()
    {
        byte[] emblem = [.. Var(1, 91_001), .. Var(2, 91_002), .. Var(3, 0x123456), .. Var(5, 0xabcdef)];
        byte[] guild = [.. Msg(1, Msg(3, emblem)), .. Var(2, int.MaxValue), .. Text(3, "Trésor"), .. Var(4, 20)];
        AssertFixture("jhe", [.. Msg(1, guild), .. Var(2, int.MinValue), .. Var(3, int.MaxValue), .. Var(4, -1)],
            new GuildMembershipEvent(new(int.MaxValue, "Trésor", 20, new(new(91_001, 91_002, 0x123456, 0xabcdef))),
                int.MinValue, int.MaxValue, -1));
        AssertFixture("jhh", [], new GuildGeneralInformationEvent("", null, 0, 0, 0, 0, 0, 0, 0));
    }

    private void AssertFixture(string alias, byte[] body, GameEvent expected)
    {
        var decoded = _codec.Decode(CodecTests.Frame(1, alias, body));
        Assert.Null(decoded.Uid);
        Assert.Equal(expected, decoded.Content);
        var envelope = _codec.ReadEnvelope(_codec.Encode(new(expected)));
        Assert.Equal(GameMessageKind.Event, envelope.Kind);
        Assert.Null(envelope.Uid);
        Assert.Equal(alias, envelope.Alias);
        Assert.Equal(body, envelope.Content.Value.ToByteArray());
    }

    private static byte[] Var(int field, long value)
    {
        using var stream = new MemoryStream();
        using (var writer = new CodedOutputStream(stream, leaveOpen: true))
        {
            writer.WriteTag(field, WireFormat.WireType.Varint);
            writer.WriteInt64(value);
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
