using Google.Protobuf;
using Jondo.Unity.Protocol.Game.Wire;
using Xunit;

namespace Jondo.Unity.Protocol.Game.Tests;

/// <summary>Independent August wire fixtures for the reviewed partial-entry additions.</summary>
public sealed class EntryCoverageTests
{
    private readonly GameCodec _codec = new();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Entry_continuations_and_weekly_reset_preserve_envelope_kind_and_signed_uid(int uid)
    {
        foreach (var (alias, request) in new (string, GameRequest)[] {
            ("kwb", new CharacterForceSelectionReadyRequest()),
            ("ijm", new FightMapRequest()),
            ("jew", new GuildWeeklyResetDateRequest()) })
        {
            var decoded = _codec.Decode(CodecTests.Frame(2, alias, [], uid));
            Assert.Equal(request, decoded.Content);
            Assert.Equal(uid, decoded.Uid);
            var encoded = _codec.ReadEnvelope(_codec.Encode(new(request, uid)));
            Assert.Equal(GameMessageKind.Request, encoded.Kind);
            Assert.Equal(alias, encoded.Alias);
            Assert.Equal(uid, encoded.Uid);
            Assert.Empty(encoded.Content.Value);
        }
        const string reset = "2026-11-10T05:00:00Z";
        var response = _codec.Decode(CodecTests.Frame(3, "jez", Text(1, reset), uid));
        Assert.Equal(new GuildWeeklyResetDateResponse(reset), response.Content);
        Assert.Equal(uid, response.Uid);
        var envelope = _codec.ReadEnvelope(_codec.Encode(new(new GuildWeeklyResetDateResponse(reset), uid)));
        Assert.Equal(GameMessageKind.Response, envelope.Kind);
        Assert.Equal("jez", envelope.Alias);
        Assert.Equal(uid, envelope.Uid);
        Assert.Equal(Text(1, reset), envelope.Content.Value.ToByteArray());
        AssertEncoded("kvd", [], new CharacterForceSelectionEvent());
        Assert.Throws<GameConversionException>(() => _codec.Decode(CodecTests.Frame(1, "kwb", [])));
        Assert.Throws<GameConversionException>(() => _codec.Decode(CodecTests.Frame(1, "jez", Text(1, reset))));
    }

    [Fact]
    public void Welcome_identities_bind_certification_session_ready_and_int64_elapsed_seconds()
    {
        Assert.Equal(new TrustStatusEvent(true), Decode("hpd", Var(1, 1)).Content);
        Assert.Equal(new ServerSessionReadyEvent(), Decode("krs", []).Content);
        Assert.Equal(new AccountSubscriptionElapsedDurationUpdateEvent(4_000_000_000L),
            Decode("mgz", Var(1, 4_000_000_000L)).Content);
        Assert.Equal(new AccountSubscriptionElapsedDurationUpdateEvent(0), Decode("mgz", []).Content);
        AssertEncoded("hpd", Var(1, 1), new TrustStatusEvent(true));
        AssertEncoded("krs", [], new ServerSessionReadyEvent());
        AssertEncoded("mgz", Var(1, 4_000_000_000L), new AccountSubscriptionElapsedDurationUpdateEvent(4_000_000_000L));
        Assert.Equal(nameof(ServerSessionReadyEvent), GameBindings.StableName("krs"));
        Assert.Equal(nameof(AccountSubscriptionElapsedDurationUpdateEvent), GameBindings.StableName("mgz"));
    }

    [Theory]
    [InlineData("2026-08-01T10:11:12.000+02:00", "2026-08-11T12:13:14.000Z")]
    [InlineData("", "")]
    public void Selection_preserves_named_date_strings_present_empty_gender_and_recursive_looks(string disconnected, string connected)
    {
        byte[] pet = Body(Var(2, 3), Var(3, 77));
        byte[] rider = Body(Var(2, 3), Var(3, 1), Msg(7, Body(Msg(1, pet), Var(4, 1))));
        byte[] mount = Body(Packed(1, [123, 456]), Var(2, 3), Var(3, 200), Packed(5, [100]),
            Packed(6, [12]), Msg(7, Body(Msg(1, rider), Var(3, 2), Var(4, 2))));
        byte[] traits = Body(Text(1, disconnected), Msg(2, []), Text(4, connected), Msg(6, mount), Var(7, 1));
        byte[] character = Body(Msg(1, Body(Text(2, "Example"), Var(3, 10), Msg(4, traits))), Var(2, 42));
        byte[] body = Msg(1, Msg(1, character));
        var selected = Assert.IsType<CharacterSelectionEventCaseSuccess>(Decode("kva", body).Content);
        var basic = Assert.IsType<CharacterComplementCaseCharacterBasicInformation>(selected.Value.Character.ComplementCase).Value;
        var look = Assert.IsType<CharacterBasicInformationComplementCaseCharacterLook>(basic.ComplementCase).Value;
        Assert.Equal(disconnected, look.LastDisconnectionDate);
        Assert.Equal(connected, look.LastConnectionDate);
        var baseInformation = Assert.IsType<CharacterLookComplementCaseBaseInformation>(look.ComplementCase).Value;
        Assert.Equal(Gender.Male, baseInformation.Gender);
        Assert.IsType<CharacterBaseInformationComplementCaseNone>(baseInformation.ComplementCase);
        var driver = Assert.Single(look.Look.SubEntities);
        Assert.Equal(2, driver.BindingPointIndex);
        Assert.Equal(SubEntityBindingPointCategory.HookPointCategoryMountDriver, driver.BindingPointCategory);
        Assert.Equal(1, driver.SubEntityLook.Bones);
        var child = Assert.Single(driver.SubEntityLook.SubEntities);
        Assert.Equal(SubEntityBindingPointCategory.HookPointCategoryPet, child.BindingPointCategory);
        Assert.Equal(77, child.SubEntityLook.Bones);
        AssertEncoded("kva", body, selected);
    }

    [Theory]
    [InlineData(0, SubEntityBindingPointCategory.HookPointCategoryUnused)]
    [InlineData(1, SubEntityBindingPointCategory.HookPointCategoryPet)]
    [InlineData(2, SubEntityBindingPointCategory.HookPointCategoryMountDriver)]
    [InlineData(3, SubEntityBindingPointCategory.HookPointCategoryLiftedEntity)]
    [InlineData(4, SubEntityBindingPointCategory.HookPointCategoryBaseBackground)]
    [InlineData(6, SubEntityBindingPointCategory.HookPointCategoryBaseForeground)]
    [InlineData(7, SubEntityBindingPointCategory.HookPointCategoryUnderwaterBubbles)]
    [InlineData(9, SubEntityBindingPointCategory.Unknown9)]
    [InlineData(10, SubEntityBindingPointCategory.Unknown10)]
    [InlineData(11, SubEntityBindingPointCategory.Unknown11)]
    [InlineData(12, SubEntityBindingPointCategory.Unknown12)]
    [InlineData(13, SubEntityBindingPointCategory.Unknown13)]
    [InlineData(14, SubEntityBindingPointCategory.Unknown14)]
    public void Subentity_category_numbers_do_not_follow_stable_ordinals(int number, SubEntityBindingPointCategory expected)
    {
        byte[] look = Body(Var(3, 1), Msg(7, Body(Msg(1, Var(3, 2)), Var(4, number))));
        byte[] body = CharacterListWithLook(look);
        var list = Assert.IsType<CharacterListEvent>(Decode("kvi", body).Content);
        var basic = Assert.IsType<CharacterComplementCaseCharacterBasicInformation>(Assert.Single(list.Characters).ComplementCase).Value;
        var characterLook = Assert.IsType<CharacterBasicInformationComplementCaseCharacterLook>(basic.ComplementCase).Value;
        Assert.Equal(expected, Assert.Single(characterLook.Look.SubEntities).BindingPointCategory);
        AssertEncoded("kvi", body, list);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    public void Undeclared_subentity_values_are_not_silently_renumbered(int number)
    {
        byte[] body = CharacterListWithLook(Body(Var(3, 1), Msg(7, Body(Msg(1, Var(3, 2)), Var(4, number)))));
        Assert.Throws<GameConversionException>(() => Decode("kvi", body));
    }

    [Fact]
    public void Map_none_case_and_all_reviewed_collections_preserve_wire_values()
    {
        byte[] options = Body(Var(1, 1), Var(2, 1), Var(3, 1), Var(4, 1));
        byte[] team = Body(Var(3, 42), Msg(5, []));
        byte[] fight = Body(Packed(1, [100, 200]), Var(2, 4), Msg(3, team), Msg(5, options), Var(6, 77));
        byte[] house = Body(Var(2, 12), Var(4, 34), Msg(7, Body(Packed(1, [101, 102]), Msg(2, []))));
        byte[] body = Body(Var(2, 42), Msg(3, Body(Var(1, 1), Var(2, 123))), Var(6, 450), Msg(9, house),
            Msg(10, Body(Msg(1, Body(Var(1, 51), Var(2, 34), Var(4, 17), Var(5, 255))), Var(2, 200))),
            Msg(11, Body(Var(1, 1), Msg(3, Body(Var(1, 1002), Var(2, 115))),
                Msg(4, Body(Var(1, 1001), Var(2, 114))), Var(5, 123), Var(6, -1))),
            Msg(12, fight), Msg(15, Body(Var(1, 1), Var(2, 200), Var(3, 123), Var(4, 2))));
        var map = Assert.IsType<MapComplementaryInformationEvent>(Decode("jss", body).Content);
        Assert.IsType<MapSpecificComplementaryInformationCaseNone>(map.SpecificCase);
        Assert.Empty(map.Actors);
        Assert.Equal(new MapObstacle(123, MapObstacleState.ObstacleClosed), Assert.Single(map.Obstacles));
        Assert.Equal(new MapCellColor(200, new(17, 34, 51, 255)), Assert.Single(map.CellColors));
        var interactive = Assert.Single(map.InteractiveElements);
        Assert.True(interactive.OnCurrentMap);
        Assert.Null(interactive.AgeBonus);
        Assert.Equal(new InteractiveElementSkill(114, 1001, null), Assert.Single(interactive.EnabledSkills));
        Assert.Equal(new InteractiveElementSkill(115, 1002, null), Assert.Single(interactive.DisabledSkills));
        Assert.Equal(new StatedElement(123, 200, 2, true), Assert.Single(map.StatedElements));
        var summary = Assert.Single(map.Fights);
        Assert.Equal(77, summary.FightId);
        Assert.Equal(FightType.PvM, summary.Type);
        Assert.Equal(new[] { 100, 200 }, summary.TeamsPosition);
        Assert.Equal(42, Assert.Single(summary.TeamsInformation).LeaderId);
        Assert.Equal(new FightOptionsInformation(true, true, true, true), Assert.Single(summary.TeamsOptions));
        var mappedHouse = Assert.Single(map.Houses);
        Assert.Equal(12, mappedHouse.HouseId);
        Assert.Equal(34, mappedHouse.ModelId);
        var street = Assert.IsType<HouseInformationCaseHouseOnMap>(mappedHouse.InformationCase).Value;
        Assert.Equal(new[] { 101, 102 }, street.DoorsOnMap);
        var instance = Assert.Single(street.HousesInformation);
        Assert.Equal(0, instance.InstanceId);
        Assert.Null(instance.Price);
        Assert.Null(instance.AccountTag);
        Assert.Empty(instance.SkillsId);
        AssertEncoded("jss", body, map);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Interactive_optional_zero_values_preserve_absence(bool present)
    {
        byte[] skill = Body(Var(1, 1001), Var(2, 114), present ? Var(3, 0) : []);
        byte[] element = Body(Var(5, 123), Msg(4, skill), present ? Var(2, 0) : []);
        byte[] body = Body(Var(2, 42), Msg(11, element));
        var map = Assert.IsType<MapComplementaryInformationEvent>(Decode("jss", body).Content);
        var interactive = Assert.Single(map.InteractiveElements);
        Assert.Equal(present ? 0 : (int?)null, interactive.AgeBonus);
        Assert.Equal(present ? 0 : (int?)null, Assert.Single(interactive.EnabledSkills).NameId);
        AssertEncoded("jss", body, map);
    }

    [Theory]
    [InlineData(0, MapObstacleState.ObstacleOpened)]
    [InlineData(1, MapObstacleState.ObstacleClosed)]
    public void Obstacle_state_has_reviewed_numeric_correspondence(int number, MapObstacleState expected)
    {
        byte[] body = Body(Var(2, 42), Msg(3, Body(Var(1, number), Var(2, 123))));
        var map = Assert.IsType<MapComplementaryInformationEvent>(Decode("jss", body).Content);
        Assert.Equal(expected, Assert.Single(map.Obstacles).State);
        AssertEncoded("jss", body, map);
    }

    [Fact]
    public void Map_none_is_distinct_from_present_empty_coordinates()
    {
        var none = Assert.IsType<MapComplementaryInformationEvent>(Decode("jss", Var(2, 42)).Content);
        var coords = Assert.IsType<MapComplementaryInformationEvent>(Decode("jss", Body(Var(2, 42), Msg(13, []))).Content);
        Assert.IsType<MapSpecificComplementaryInformationCaseNone>(none.SpecificCase);
        Assert.Equal(new MapSpecificComplementaryInformationCaseCoords(new(0, 0)), coords.SpecificCase);
        AssertEncoded("jss", Var(2, 42), none);
        AssertEncoded("jss", Body(Var(2, 42), Msg(13, [])), coords);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Fight_option_flags_are_independently_bound(int tag)
    {
        byte[] body = Body(Var(2, 42), Msg(12, Body(Var(6, 77), Msg(5, Var(tag, 1)))));
        var map = Assert.IsType<MapComplementaryInformationEvent>(Decode("jss", body).Content);
        var options = Assert.Single(Assert.Single(map.Fights).TeamsOptions);
        Assert.Equal(tag == 1, options.IsClosed);
        Assert.Equal(tag == 2, options.IsSecret);
        Assert.Equal(tag == 3, options.IsAskingForHelp);
        Assert.Equal(tag == 4, options.IsRestrictedToParty);
        AssertEncoded("jss", body, map);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(6)]
    public void House_information_branches_preserve_present_empty_coordinates_and_instances(int branch)
    {
        // Present-empty coordinates differ from absence, including extended coordinates.
        int coordinateTag = branch == 5 ? 5 : 1;
        byte[] information = Body(Msg(coordinateTag, []), Msg(2, []));
        byte[] house = Body(Var(2, 12), Var(4, 34), Msg(branch, information));
        byte[] body = Body(Var(2, 42), Msg(9, house));
        var map = Assert.IsType<MapComplementaryInformationEvent>(Decode("jss", body).Content);
        var actual = Assert.Single(map.Houses).InformationCase;
        if (branch == 1) Assert.IsType<HouseInformationCaseHouseInside>(actual);
        else if (branch == 5) Assert.IsType<HouseInformationCaseHouseGuild>(actual);
        else Assert.IsType<HouseInformationCaseHouseAccount>(actual);
        AssertEncoded("jss", body, map);
    }

    [Fact]
    public void Unreviewed_house_instance_fields_remain_raw_only_not_falsely_lossless()
    {
        byte[] house = Body(Var(2, 12), Var(4, 34), Msg(7, Msg(2, Body(Var(2, 1), Var(7, 12345), Var(12, 56)))));
        byte[] body = Body(Var(2, 42), Msg(9, house));
        var decoded = Decode("jss", body);
        byte[] restored = _codec.ReadEnvelope(_codec.Encode(decoded)).Content.Value.ToByteArray();
        Assert.NotEqual(_codec.DecodeRawPayload("jss", body), _codec.DecodeRawPayload("jss", restored));
        Assert.Equal(body, _codec.DecodeRawPayload("jss", body).ToByteArray());
    }

    private GameFrame Decode(string alias, byte[] body) => _codec.Decode(CodecTests.Frame(1, alias, body));

    private void AssertEncoded(string alias, byte[] expected, GameEvent message)
    {
        var envelope = _codec.ReadEnvelope(_codec.Encode(new(message)));
        Assert.Equal(alias, envelope.Alias);
        Assert.Equal(GameMessageKind.Event, envelope.Kind);
        Assert.Null(envelope.Uid);
        Assert.Equal(_codec.DecodeRawPayload(alias, expected), _codec.DecodeRawPayload(alias, envelope.Content.Value.Span));
    }

    private static byte[] CharacterListWithLook(byte[] look) => Msg(1, Body(
        Msg(1, Body(Text(2, "Example"), Var(3, 10), Msg(4, Body(Msg(2, []), Msg(6, look), Var(7, 1))))), Var(2, 42)));

    private static byte[] Body(params byte[][] fields) => fields.SelectMany(f => f).ToArray();

    private static byte[] Var(int tag, long value) => Write(o => { o.WriteTag(tag, WireFormat.WireType.Varint); o.WriteInt64(value); });

    private static byte[] Msg(int tag, byte[] value) => Write(o => { o.WriteTag(tag, WireFormat.WireType.LengthDelimited); o.WriteBytes(ByteString.CopyFrom(value)); });

    private static byte[] Text(int tag, string value) => Write(o => { o.WriteTag(tag, WireFormat.WireType.LengthDelimited); o.WriteString(value); });

    private static byte[] Packed(int tag, int[] values) => Msg(tag, Write(o => { foreach (int value in values) o.WriteInt32(value); }));

    private static byte[] Write(Action<CodedOutputStream> write)
    {
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, true)) { write(output); output.Flush(); }
        return stream.ToArray();
    }
}
