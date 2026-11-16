using Google.Protobuf;
using Jondo.Unity.Protocol.Game.Wire;
using Xunit;

namespace Jondo.Unity.Protocol.Game.Tests;

public sealed class PrismListContractTests
{
    private readonly GameCodec _codec = new();

    [Fact]
    public void Populated_list_has_independent_coordinates_dates_alliance_crystal_and_effect_wire_values()
    {
        const string uid = "00000000-0000-4000-8000-000000009672";
        byte[] alliance = Body(Msg(2, Body(Var(1, 101), Var(2, 202), Var(3, 303), Var(5, 404))), Str(3, "DistinctAlliance"), Str(4, uid), Str(5, "XYZ"));
        byte[] item = Body(Var(1, 91_001), Msg(2, Body(Var(4, 73), Var(11, 91_011))), Var(3, 1), Var(4, 2_000_067_123));
        byte[] crystal = Body(Msg(2, Msg(5, item)), Var(3, 1));
        byte[] state = Body(Var(1, 0), Var(2, 8_000_000_007), Var(3, 3), Var(4, 1_784_886_505), Msg(5, alliance), Msg(6, crystal));
        byte[] body = Msg(1, Body(Var(1, 9_968_000_001), Msg(2, Body(Var(1, -37), Var(2, 59))), Var(3, 81_001), Msg(4, state)));
        var expected = new PrismListEvent([new(9_968_000_001, new(-37, 59), 81_001,
            new(0, 8_000_000_007, PrismState.Protected, 1_784_886_505,
                new("DistinctAlliance", new(101, 202, 303, 404), uid, "XYZ", null),
                new(new(0, false, new(91_001, [new(91_011, new ObjectEffectCaseValueInt(73))], 1, 2_000_067_123, [])), PrismCristalType.Protective)))]);
        AssertBody(body, expected);
        var prism = Assert.Single(Decode(body).Prisms);
        Assert.Equal(9_968_000_001, prism.MapId);
        Assert.Equal(new MapCoordinates(-37, 59), prism.MapCoordinates);
        Assert.Equal(0, prism.Prism!.CristalNumberLeft);
        Assert.Equal(8_000_000_007, prism.Prism.NextEvolutionDate);
        Assert.Equal(1_784_886_505, prism.Prism.PlacementDate);
        Assert.Equal(expected.Prisms[0].Prism!.AllianceInformation, prism.Prism.AllianceInformation);
        var held = prism.Prism.Cristal!;
        Assert.Equal(PrismCristalType.Protective, held.Type);
        Assert.Equal(2_000_067_123, held.CristalObject.Item.Uid);
        Assert.Equal(new ObjectEffect(91_011, new ObjectEffectCaseValueInt(73)), Assert.Single(held.CristalObject.Item.Effects));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Own_empty_crystal_has_both_wrappers_and_optional_zero_while_foreign_view_omits_them(bool own)
    {
        byte[] foreign = Body(Var(2, 1_784_886_505_835), Var(4, 1_784_886_505));
        byte[] state = own ? Body(Var(1, 0), foreign, Msg(6, Msg(2, Msg(5, [])))) : foreign;
        byte[] body = Msg(1, Body(Msg(2, []), Msg(4, state)));
        var expected = new PrismListEvent([new(0, new(0, 0), 0,
            new(own ? 0 : null, 1_784_886_505_835, PrismState.Normal, 1_784_886_505, null,
                own ? new(new(0, false, new(0, [], 0, 0, [])), PrismCristalType.NoCristal) : null))]);
        AssertBody(body, expected);
        var prism = Assert.Single(Decode(body).Prisms).Prism!;
        Assert.Equal(own ? 0 : (int?)null, prism.CristalNumberLeft);
        if (own) Assert.Equal(0, prism.Cristal!.CristalObject.Item.Gid);
        else Assert.Null(prism.Cristal);
    }

    [Fact]
    public void Empty_list_and_present_empty_prism_are_distinct()
    {
        AssertBody([], new([]));
        Assert.Empty(Decode([]).Prisms);
        AssertBody([0x0a, 0], new([new(0, null, 0, null)]));
        var empty = Assert.Single(Decode([0x0a, 0]).Prisms);
        Assert.Null(empty.MapCoordinates);
        Assert.Null(empty.Prism);
    }

    [Fact]
    public void Legacy_localized_uuid_is_raw_retained_but_not_copied_into_semantic_sends()
    {
        byte[] mapped = Msg(1, Var(1, 9_968_000_001));
        byte[] legacy = Msg(1, Body(Var(1, 9_968_000_001), Str(5, "legacy-duplicate-uuid")));
        Assert.NotEqual(_codec.DecodeRawPayload("ife", mapped), _codec.DecodeRawPayload("ife", legacy));
        AssertBody(mapped, Decode(legacy));
    }

    private PrismListEvent Decode(byte[] body)
        => Assert.IsType<PrismListEvent>(_codec.Decode(CodecTests.Frame(1, "ife", body)).Content);
    private void AssertBody(byte[] expected, PrismListEvent message)
    {
        var envelope = _codec.ReadEnvelope(_codec.Encode(new(message)));
        Assert.Equal("ife", envelope.Alias);
        Assert.Equal(GameMessageKind.Event, envelope.Kind);
        Assert.Null(envelope.Uid);
        Assert.Equal(expected, envelope.Content.Value.ToByteArray());
    }
    private static byte[] Body(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
    private static byte[] Var(int tag, long value) => Write(output => { output.WriteTag(tag, WireFormat.WireType.Varint); output.WriteInt64(value); });
    private static byte[] Msg(int tag, byte[] value) => Write(output => { output.WriteTag(tag, WireFormat.WireType.LengthDelimited); output.WriteBytes(ByteString.CopyFrom(value)); });
    private static byte[] Str(int tag, string value) => Write(output => { output.WriteTag(tag, WireFormat.WireType.LengthDelimited); output.WriteString(value); });
    private static byte[] Write(Action<CodedOutputStream> write)
    {
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, leaveOpen: true)) write(output);
        return stream.ToArray();
    }
}
