using Google.Protobuf;
using Jondo.Unity.Protocol.Game.Wire;
using Xunit;

namespace Jondo.Unity.Protocol.Game.Tests;

public sealed class GuildHallMappingTests
{
    private readonly GameCodec _codec = new();
    private const long MapId = 9_000_000_001;

    [Theory]
    [InlineData(1791, 0x123456, 0xabcdef)]
    [InlineData(-1, 0, 0)]
    [InlineData(int.MinValue, int.MaxValue, -1)]
    [InlineData(0, 0, 0)]
    public void Customization_binds_distinct_signed_skin_and_color_values(int skinId, int color7, int color8)
    {
        byte[] customization = [.. Scalar(2, skinId), .. Scalar(3, color7), .. Scalar(5, color8)];
        byte[] body = MapBody(Msg(2, customization));
        var expected = new MapComplementaryGuildInformationCaseGuildHallCustomizationInformation(new(skinId, color7, color8));

        Assert.Equal(expected, GuildCase(Decode(body)));
        AssertEncoded(body, Snapshot(expected));
    }

    [Fact]
    public void Selection_preserves_its_selected_empty_branch_and_semantic_json_name()
    {
        byte[] body = MapBody(Msg(3, []));
        var expected = new MapComplementaryGuildInformationCaseGuildHallSelectionInformation(new());
        Assert.Equal(expected, GuildCase(Decode(body)));
        AssertEncoded(body, Snapshot(expected));

        string json = System.Text.Json.JsonSerializer.Serialize<MapComplementaryGuildInformationCase>(expected);
        Assert.Contains("\"$type\":\"GuildHallSelectionInformation\"", json);
        Assert.IsType<MapSpecificComplementaryInformationCaseNone>(Decode(Var(2, MapId)).SpecificCase);
    }

    [Fact]
    public void Own_hall_retains_theme_and_available_themes_in_its_separate_branch()
    {
        byte[] theme = [.. Var(1, 7), .. Msg(2, [2, 7, 11])];
        byte[] body = MapBody(Msg(1, Msg(1, theme)));
        var own = Assert.IsType<MapComplementaryGuildInformationCaseGuildHallInformation>(GuildCase(Decode(body))).Value;
        Assert.NotNull(own.ThemeInformation);
        Assert.Equal(7, own.ThemeInformation.ThemeId);
        Assert.Equal(new[] { 2, 7, 11 }, own.ThemeInformation.AvailableThemeIds);
        AssertEncoded(body, Snapshot(new MapComplementaryGuildInformationCaseGuildHallInformation(new(new(7, [2, 7, 11])))));
    }

    [Fact]
    public void Selection_boolean_remains_raw_only_while_selected_branch_survives()
    {
        byte[] body = MapBody(Msg(3, Var(1, 1)));
        var decoded = Decode(body);
        Assert.IsType<MapComplementaryGuildInformationCaseGuildHallSelectionInformation>(GuildCase(decoded));
        Assert.Equal(body, _codec.DecodeRawPayload("jss", body).ToByteArray());
        AssertEncoded(MapBody(Msg(3, [])), decoded);
    }

    [Fact]
    public void Unmapped_customization_fields_are_retained_raw_and_omitted_from_semantic_encoding()
    {
        byte[] customization = [.. Msg(1, []), .. Var(2, 1791), .. Var(3, 0x123456), .. Var(4, 73), .. Var(5, 0xabcdef)];
        byte[] body = MapBody(Msg(2, customization));
        var decoded = Decode(body);
        var expected = new MapComplementaryGuildInformationCaseGuildHallCustomizationInformation(new(1791, 0x123456, 0xabcdef));
        Assert.Equal(expected, GuildCase(decoded));
        Assert.Equal(body, _codec.DecodeRawPayload("jss", body).ToByteArray());
        AssertEncoded(MapBody(Msg(2, [.. Var(2, 1791), .. Var(3, 0x123456), .. Var(5, 0xabcdef)])), decoded);
    }

    private static MapComplementaryInformationEvent Snapshot(MapComplementaryGuildInformationCase guildCase)
        => new(MapId, [], 0, [], [], [], [], [], [],
            new MapSpecificComplementaryInformationCaseGuildInformation(new(guildCase)));

    private static MapComplementaryGuildInformationCase GuildCase(MapComplementaryInformationEvent map)
        => Assert.IsType<MapSpecificComplementaryInformationCaseGuildInformation>(map.SpecificCase).Value.ComplementaryInformationCase;

    private MapComplementaryInformationEvent Decode(byte[] body)
    {
        var frame = _codec.Decode(CodecTests.Frame(1, "jss", body));
        Assert.Null(frame.Uid);
        var map = Assert.IsType<MapComplementaryInformationEvent>(frame.Content);
        Assert.Equal(MapId, map.MapId);
        return map;
    }

    private void AssertEncoded(byte[] expected, MapComplementaryInformationEvent map)
    {
        var envelope = _codec.ReadEnvelope(_codec.Encode(new(map)));
        Assert.Equal(GameMessageKind.Event, envelope.Kind);
        Assert.Null(envelope.Uid);
        Assert.Equal("jss", envelope.Alias);
        Assert.Equal(_codec.DecodeRawPayload("jss", expected), _codec.DecodeRawPayload("jss", envelope.Content.Value.Span));
    }

    private static byte[] MapBody(byte[] guild) => [.. Var(2, MapId), .. Msg(4, guild)];
    private static byte[] Scalar(int tag, int value) => value == 0 ? [] : Var(tag, value);
    private static byte[] Var(int tag, long value) => Write(output =>
    {
        output.WriteTag(tag, WireFormat.WireType.Varint);
        output.WriteInt64(value);
    });

    private static byte[] Msg(int tag, byte[] value) => Write(output =>
    {
        output.WriteTag(tag, WireFormat.WireType.LengthDelimited);
        output.WriteBytes(ByteString.CopyFrom(value));
    });

    private static byte[] Write(Action<CodedOutputStream> write)
    {
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, leaveOpen: true)) write(output);
        return stream.ToArray();
    }
}
