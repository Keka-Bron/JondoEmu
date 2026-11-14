using Google.Protobuf;
using Jondo.Unity.Protocol.Game.Wire;
using Xunit;

namespace Jondo.Unity.Protocol.Game.Tests;

/// <summary>Independent house fixtures formerly asserted through retired server byte builders.</summary>
public sealed class MapHouseContractTests
{
    private const long MapId = 9_000_000_001;
    private readonly GameCodec _codec = new();

    [Fact]
    public void Street_house_preserves_door_instance_account_admin_lock_and_shared_guild()
    {
        // Pandala street-house fixture: the root map body wraps this house at tag 9.
        byte[] house = Convert.FromHexString("10d9da1f2097033a460a03c4e31f123f1a210a0c1a0a08e7011020288fd2bf0710982c1a0c4775696c642d53616d706c65200f2001280142120a0a486f7573654f776e657212043030303448016004");
        byte[] expected = Body(Var(2, MapId), Msg(9, house));
        var guild = new GuildInformation(5656, "Guild-Sample", 15, new(new(231, 32, 0, 15_722_767)));
        var snapshot = new MapComplementaryInformationEvent(MapId, [], 0, [],
            [new(519513, 407, new HouseInformationCaseHouseOnMap(new([520644], [Instance(4, "HouseOwner", "0004", guild)])))],
            [], [], [], [], new MapSpecificComplementaryInformationCaseNone());

        AssertEncoded(expected, snapshot);
        var decoded = Decode(expected);
        var street = Assert.IsType<HouseInformationCaseHouseOnMap>(Assert.Single(decoded.Houses).InformationCase).Value;
        Assert.Equal(new[] { 520644 }, street.DoorsOnMap);
        AssertInstance(Assert.Single(street.HousesInformation), 4, "HouseOwner", "0004", guild);
    }

    [Fact]
    public void Interior_house_preserves_negative_street_coordinates_and_its_distinct_guild_emblem()
    {
        // Interior-house fixture: its tag-1 current house lives under root tag 7.
        byte[] inside = Convert.FromHexString("0a640a5b0a1608e2ffffffffffffffff0110cbffffffffffffffff0112411a210a111a0f08ce0210221885e2ef072896d2820110be351a074775696c642d4120042001280142140a0c4f776e65724163636f756e7412043030303148016016109cf31f20ba03");
        byte[] expected = Body(Var(2, MapId), Msg(7, inside));
        var guild = new GuildInformation(6846, "Guild-A", 4, new(new(334, 34, 16_511_237, 2_140_438)));
        var snapshot = new MapComplementaryInformationEvent(MapId, [], 0, [], [], [], [], [], [],
            new MapSpecificComplementaryInformationCaseInHouseInformation(new(new(522652, 442,
                new HouseInformationCaseHouseInside(new(new(-30, -53), Instance(22, "OwnerAccount", "0001", guild)))))));

        AssertEncoded(expected, snapshot);
        var house = Assert.IsType<MapSpecificComplementaryInformationCaseInHouseInformation>(Decode(expected).SpecificCase).Value.CurrentHouse;
        Assert.Equal(522652, house.HouseId);
        Assert.Equal(442, house.ModelId);
        var interior = Assert.IsType<HouseInformationCaseHouseInside>(house.InformationCase).Value;
        Assert.Equal(new MapCoordinates(-30, -53), interior.Coordinates);
        AssertInstance(interior.HouseInformation, 22, "OwnerAccount", "0001", guild);
    }

    private static HouseInstance Instance(int instanceId, string nickname, string tag, GuildInformation guild)
        => new(false, guild, true, true, 0, null, new(nickname, tag), true, 0, [], instanceId);

    private static void AssertInstance(HouseInstance actual, int instanceId, string nickname, string tag, GuildInformation guild)
    {
        Assert.Equal(instanceId, actual.InstanceId);
        Assert.True(actual.HasOwner);
        Assert.True(actual.IsAdminLocked);
        Assert.True(actual.IsLocked);
        Assert.False(actual.IsSaleLocked);
        Assert.Null(actual.Price);
        Assert.Equal(new AccountTag(nickname, tag), actual.AccountTag);
        Assert.Equal(guild, actual.GuildInformation);
        Assert.Equal(0, actual.RoomsCount);
        Assert.Equal(0, actual.ChestsCount);
        Assert.Empty(actual.SkillsId);
    }

    private MapComplementaryInformationEvent Decode(byte[] body)
        => Assert.IsType<MapComplementaryInformationEvent>(_codec.Decode(CodecTests.Frame(1, "jss", body)).Content);

    private void AssertEncoded(byte[] expected, MapComplementaryInformationEvent snapshot)
    {
        var envelope = _codec.ReadEnvelope(_codec.Encode(new(snapshot)));
        Assert.Equal(GameMessageKind.Event, envelope.Kind);
        Assert.Equal("jss", envelope.Alias);
        Assert.Null(envelope.Uid);
        Assert.Equal(_codec.DecodeRawPayload("jss", expected), _codec.DecodeRawPayload("jss", envelope.Content.Value.Span));
    }

    private static byte[] Body(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
    private static byte[] Var(int tag, long value) => Write(output => { output.WriteTag(tag, WireFormat.WireType.Varint); output.WriteInt64(value); });
    private static byte[] Msg(int tag, byte[] value) => Write(output => { output.WriteTag(tag, WireFormat.WireType.LengthDelimited); output.WriteBytes(ByteString.CopyFrom(value)); });
    private static byte[] Write(Action<CodedOutputStream> write)
    {
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, leaveOpen: true)) write(output);
        return stream.ToArray();
    }
}
