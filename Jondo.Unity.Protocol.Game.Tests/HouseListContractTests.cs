using Google.Protobuf;
using Jondo.Unity.Protocol.Game.Wire;
using Xunit;

namespace Jondo.Unity.Protocol.Game.Tests;

public sealed class HouseListContractTests
{
    private readonly GameCodec _codec = new();

    [Theory]
    [InlineData(20_500_000L, "0a54109cf31f20ba03324b0a1e088898b06510e2ffffffffffffffff0120cd0728cbffffffffffffffff0112292001300338a09ce30942140a0c4f776e65724163636f756e74120430303031480150025a02d3026016")]
    [InlineData(0L, "0a4f109cf31f20ba0332460a1e088898b06510e2ffffffffffffffff0120cd0728cbffffffffffffffff0112242001300342140a0c4f776e65724163636f756e74120430303031480150025a02d3026016")]
    public void Account_list_matches_independent_sale_and_withdrawal_fixtures(long price, string hex)
    {
        var instance = new HouseInstance(false, null, true, false, 3, price == 0 ? null : price,
            new("OwnerAccount", "0001"), true, 2, [339], 22);
        var house = new House(522652, 442, new HouseInformationCaseHouseAccount(new(new(212601864, -30, 973, -53), instance)));
        AssertBody("jaa", hex, new HouseAccountEvent([house]));
        var decoded = Assert.Single(Assert.IsType<HouseAccountEvent>(_codec.Decode(CodecTests.Frame(1, "jaa", Convert.FromHexString(hex))).Content).Houses);
        var account = Assert.IsType<HouseInformationCaseHouseAccount>(decoded.InformationCase).Value;
        Assert.Equal(price == 0 ? null : (long?)price, account.HouseInformation.Price);
        Assert.Null(account.HouseInformation.GuildInformation);
        Assert.Equal(22, account.HouseInformation.InstanceId);
        Assert.Equal(new MapExtendedCoordinates(212601864, -30, 973, -53), account.Coordinates);
    }

    [Fact]
    public void Guild_list_matches_independent_fixture_with_rights_and_separate_admin_lock()
    {
        const string hex = "124510b1db1f20a8032a3c1220200128013003420e0a066f776e657232120430303032480150025a02d302601018f9032a15088084d06810e7ffffffffffffffff0120d1072822";
        var instance = new HouseInstance(false, null, true, true, 3, null, new("owner2", "0002"), true, 2, [339], 16);
        AssertBody("jjs", hex, new GuildHousesEvent([new(519601, 424,
            new HouseInformationCaseHouseGuild(new(new(219415040, -25, 977, 34), instance, 505)))]));
        var house = Assert.Single(Assert.IsType<GuildHousesEvent>(_codec.Decode(CodecTests.Frame(1, "jjs", Convert.FromHexString(hex))).Content).Houses);
        var guild = Assert.IsType<HouseInformationCaseHouseGuild>(house.InformationCase).Value;
        Assert.Equal(505, guild.GuildShareParams);
        Assert.True(guild.HouseInformation.IsAdminLocked);
        Assert.False(guild.HouseInformation.IsSaleLocked);
        Assert.True(guild.HouseInformation.IsLocked);
        Assert.Null(guild.HouseInformation.GuildInformation);
    }

    [Theory]
    [InlineData("jaa")]
    [InlineData("jjs")]
    public void Empty_lists_are_empty_events(string alias)
        => AssertBody(alias, "", alias == "jaa" ? new HouseAccountEvent([]) : new GuildHousesEvent([]));

    [Fact]
    public void Large_map_and_price_and_present_zero_price_have_independent_wire_values()
    {
        // tag 1: house; house tag 6: account; account tag 1: coordinates,
        // tag 2: instance. Map/price values are 2^32 and optional zero, respectively.
        const string hex = "0a10320e0a06088080808010120438006007";
        var house = new House(0, 0, new HouseInformationCaseHouseAccount(new(new(4_294_967_296, 0, 0, 0),
            new(false, null, false, false, 0, 0, null, false, 0, [], 7))));
        AssertBody("jaa", hex, new HouseAccountEvent([house]));
        var listed = Assert.Single(Assert.IsType<HouseAccountEvent>(_codec.Decode(CodecTests.Frame(1, "jaa", Convert.FromHexString(hex))).Content).Houses);
        var account = Assert.IsType<HouseInformationCaseHouseAccount>(listed.InformationCase).Value;
        Assert.Equal(4_294_967_296, account.Coordinates.MapId);
        Assert.Equal(0L, account.HouseInformation.Price);
        AssertBody("jaa", "0a1432120a0608808080801012083880808080106007", new HouseAccountEvent([house with
        {
            InformationCase = new HouseInformationCaseHouseAccount(account with { HouseInformation = account.HouseInformation with { Price = 4_294_967_296 } }),
        }]));
    }

    private void AssertBody(string alias, string hex, GameEvent message)
    {
        var envelope = _codec.ReadEnvelope(_codec.Encode(new(message)));
        Assert.Equal(alias, envelope.Alias);
        Assert.Equal(GameMessageKind.Event, envelope.Kind);
        Assert.Null(envelope.Uid);
        Assert.Equal(Convert.FromHexString(hex), envelope.Content.Value.ToByteArray());
    }
}
