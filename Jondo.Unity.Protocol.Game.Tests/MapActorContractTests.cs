using Google.Protobuf;
using Jondo.Unity.Protocol.Game.Wire;
using Xunit;

namespace Jondo.Unity.Protocol.Game.Tests;

/// <summary>Independent populated fixtures for the map actor contracts used by direct sends.</summary>
public sealed class MapActorContractTests
{
    private readonly GameCodec _codec = new();
    private static readonly EntityLook Look = new([], 3, 91, [115], [], []);
    private static byte[] LookBytes => Body(Var(2, 3), Var(3, 91), Packed(5, [115]));
    private static readonly AllianceInformation Alliance = new("Alliance", new(12, 23, 34, 45), "alliance-id", "TAG", null);
    private static byte[] AllianceBytes => Body(Msg(2, Body(Var(1, 12), Var(2, 23), Var(3, 34), Var(5, 45))),
        Text(3, "Alliance"), Text(4, "alliance-id"), Text(5, "TAG"));

    [Fact]
    public void Player_alignment_hierarchy_restrictions_and_options_have_independent_wire_expectations()
    {
        var human = new HumanoidInformation(new(0, Alignment.Evil, null, 6, 200), null, 9_000_000_007,
            [new(new HumanOptionCaseTitleOption(new("Captain", 17))),
             new(new HumanOptionCaseOrnamentOption(new(253, null, null, 29))),
             new(new HumanOptionCaseSpeedMultiplier(1))],
            Hierarchy.RolePlayer, [Restriction.CannotAttackAsMutant], Gender.Male);
        var expected = Actor(new RolePlayActorCaseNamedActor(new("Visitor", new NamedActorCaseHumanoidInformation(human))));
        byte[] body = ActorBytes(5, Body(Text(1, "Visitor"), Msg(3, Body(
            Msg(1, Body(Var(2, 4), Var(4, 6), Var(5, 200))), Var(3, 9_000_000_007),
            Msg(5, Msg(2, Body(Text(1, "Captain"), Var(2, 17)))),
            Msg(5, Msg(9, Body(Var(1, 253), Var(4, 29)))), Msg(5, Var(7, 1)),
            Var(6, 1), Packed(7, [11])))));
        AssertEncoded("jsn", Msg(1, body), new GameRolePlayShowActorsEvent([expected]));
        var decoded = Assert.IsType<NamedActorCaseHumanoidInformation>(Assert.IsType<RolePlayActorCaseNamedActor>(Roleplay(DecodeActor(body)).ActorCase).Value.ActorCase).Value;
        Assert.Equal(Alignment.Evil, decoded.AlignmentInformation!.Alignment);
        Assert.Equal(6, decoded.AlignmentInformation.AlignmentGrade);
        Assert.Equal(9_000_000_007, decoded.AccountId);
        Assert.Equal(new[] { Restriction.CannotAttackAsMutant }, decoded.Restrictions);
        Assert.Equal(253, Assert.IsType<HumanOptionCaseOrnamentOption>(decoded.Options[1].OptionCase).Value.PlayerLevel);
    }

    [Fact]
    public void Monster_groups_preserve_underling_looks_absent_leader_looks_and_modular_alternatives()
    {
        byte[] main = Body(Var(1, 31), Var(2, 99), Var(4, 6));
        byte[] underling = Body(Var(1, 32), Var(2, 77), Msg(3, LookBytes), Var(4, 3));
        byte[] variant = Body(Msg(1, main), Var(2, 1));
        byte[] group = Body(Var(1, 1), Msg(2, Body(Msg(1, underling), Msg(2, main), Msg(3, variant))), Var(5, -1));
        var identification = new MonsterGroupStaticInformation([new(32, 77, Look, 3)], new(31, 99, null, 6),
            [new([new(31, 99, null, 6)], 1)]);
        AssertEncoded("jsn", Msg(1, ActorBytes(4, group)), new GameRolePlayShowActorsEvent([
            Actor(new RolePlayActorCaseMonsterGroupActor(new(Alignment.Without, identification, null, -1)))]));
        var decoded = Assert.IsType<RolePlayActorCaseMonsterGroupActor>(Roleplay(DecodeActor(ActorBytes(4, group))).ActorCase).Value;
        Assert.Null(decoded.Identification.MainCreature.Look);
        Assert.Equal(91, Assert.Single(decoded.Identification.Underlings).Look!.Bones);
        Assert.Null(Assert.Single(Assert.Single(decoded.Identification.Alternatives).Monsters).Look);
    }

    [Fact]
    public void Dungeon_alternatives_keep_the_captured_member_order_levels_grades_and_absent_looks()
    {
        var members = new[] { 149, 134, 101, 148, 148, 101, 134, 149 }
            .Select(id => new MonsterInGroupInformation(id, 22, null, 1)).ToArray();
        var alternatives = Enumerable.Range(4, 5)
            .Select(size => new MonstersInGroupAlternativeInformation(members.Take(size).ToArray(), size == 4 ? 1 : size)).ToArray();
        byte[] captured = Convert.FromHexString(
            "1a250a07089501101620010a07088601101620010a060865101620010a070894011016200110011a2e0a0708950110" +
            "1620010a07088601101620010a060865101620010a07089401101620010a070894011016200110051a360a070895" +
            "01101620010a07088601101620010a060865101620010a07089401101620010a07089401101620010a0608651016" +
            "200110061a3f0a07089501101620010a07088601101620010a060865101620010a07089401101620010a07089401" +
            "101620010a060865101620010a070886011016200110071a480a07089501101620010a07088601101620010a0608" +
            "65101620010a07089401101620010a07089401101620010a060865101620010a07088601101620010a0708950110" +
            "1620011008");
        var actor = Actor(new RolePlayActorCaseMonsterGroupActor(new(Alignment.Unknown,
            new([], members[0], alternatives), null, 0)));
        AssertEncoded("jsn", Msg(1, ActorBytes(4, Msg(2, Body(Msg(2, Body(Var(1, 149), Var(2, 22), Var(4, 1))), captured)))),
            new GameRolePlayShowActorsEvent([actor]));
    }

    [Fact]
    public void Monster_look_absence_is_distinct_from_a_present_empty_look()
    {
        foreach (bool present in new[] { false, true })
        {
            byte[] main = Body(Var(1, 31), present ? Msg(3, []) : []);
            byte[] body = ActorBytes(4, Msg(2, Msg(2, main)));
            var decoded = Assert.IsType<RolePlayActorCaseMonsterGroupActor>(Roleplay(DecodeActor(body)).ActorCase).Value;
            Assert.Equal(present, decoded.Identification.MainCreature.Look != null);
            AssertEncoded("jsn", Msg(1, body), new GameRolePlayShowActorsEvent([
                Actor(new RolePlayActorCaseMonsterGroupActor(new(Alignment.Unknown,
                    new([], new(31, 0, present ? new([], 0, 0, [], [], []) : null, 0), []), null, 0)))]));
        }
    }

    [Fact]
    public void Npc_tax_collector_prism_and_portal_have_distinct_typed_identity_branches()
    {
        var cases = new (int Tag, byte[] Bytes, RolePlayActorCase Expected)[]
        {
            (7, Body(Msg(2, Body(Packed(1, [41]), Packed(3, [51, 52]))), Var(3, 1), Var(5, 2892)),
                new RolePlayActorCaseNpcStaticInformation(new("", new([41], [51, 52]), Gender.Female, 0, 2892, []))),
            (6, Msg(1, Body(Msg(1, AllianceBytes), Var(2, 253), Text(3, "collector-id"), Var(4, 9_000_000_002), Var(5, 154))),
                new RolePlayActorCaseTaxCollectorActor(new(new(Alliance, 253, "collector-id", 9_000_000_002, 154), 0))),
            (2, Body(Var(2, 1_794_456_789_123), Var(3, 3), Var(4, 1_794_456_700), Msg(5, AllianceBytes)),
                new RolePlayActorCasePrismActor(new(null, 1_794_456_789_123, PrismState.Protected, 1_794_456_700, Alliance, null))),
            (3, Body(Var(2, 7), Var(3, 89)), new RolePlayActorCasePortalActor(new(7, 89))),
        };
        foreach (var (tag, bytes, expected) in cases)
        {
            byte[] actor = ActorBytes(tag, bytes);
            Assert.Equal(expected.GetType(), Roleplay(DecodeActor(actor)).ActorCase.GetType());
            AssertEncoded("jsn", Msg(1, actor), new GameRolePlayShowActorsEvent([Actor(expected)]));
        }
    }

    [Fact]
    public void Captured_tax_collector_actor_preserves_names_owner_emblem_and_complete_look()
    {
        var alliance = new AllianceInformation("Alliance2", new(104, 8, 0, 11862276),
            "00000000-0000-4000-8000-000000000005", "BLOOD", null);
        var look = new EntityLook([0x08b50104, 0x07000000], 3, 7312, [115], [1647], []);
        var actor = new ActorPositionInformation(new(352, Direction.SouthEast, null, null),
            new(look, new ActorInformationCaseRolePlayActor(new(new RolePlayActorCaseTaxCollectorActor(new(
                new(alliance, 74, "00000000-0000-4000-8000-000000000007", 240000000014, 154), 0))))), -20006);
        byte[] captured = Convert.FromHexString(
            "0a0508e00210011295010a7b32790a770a43120908681008288482d4051a09416c6c69616e636532222430303030" +
            "303030302d303030302d343030302d383030302d3030303030303030303030352a05424c4f4f44104a1a24303030" +
            "30303030302d303030302d343030302d383030302d303030303030303030303037208ec0f588fe06289a011a160a" +
            "088482d4458080803810031890392a01733202ef0c18dae3feffffffffffff01");
        AssertEncoded("jsn", Msg(1, captured), new GameRolePlayShowActorsEvent([actor]));
    }

    [Fact]
    public void Captured_prism_actor_preserves_millisecond_evolution_second_placement_and_look()
    {
        var alliance = new AllianceInformation("Alliance2", new(104, 8, 0, 11862276),
            "00000000-0000-4000-8000-000000000005", "ALLY2", null);
        var look = new EntityLook([0x01c2e5ee, 0x09000000, 0x0ab50104], 3, 2211, [], [2673], []);
        var actor = new ActorPositionInformation(new(524, Direction.SouthEast, null, null),
            new(look, new ActorInformationCaseRolePlayActor(new(new RolePlayActorCasePrismActor(
                new(null, 1784886505835, PrismState.Normal, 1784886505, alliance, null))))), -20000);
        byte[] captured = Convert.FromHexString(
            "0a05088c041001126f0a54125210ebe29a9cf93320e9e98cd3062a43120908681008288482d4051a09416c6c69" +
            "616e636532222430303030303030302d303030302d343030302d383030302d3030303030303030303030352a05" +
            "414c4c59321a170a0ceecb8b0e808080488482d455100318a3113202f11418e0e3feffffffffffff01");
        AssertEncoded("jsn", Msg(1, captured), new GameRolePlayShowActorsEvent([actor]));
    }

    [Fact]
    public void Haven_bag_owner_keeps_a_name_and_level_with_no_fabricated_character_complement()
    {
        byte[] owner = Body(Msg(1, Body(Text(2, "Owner"), Var(3, 253))), Var(2, 9_000_000_002));
        // Reviewed August meanings: theme is tag 1, room is tag 2. The retired
        // map builder supplied these backwards; the semantic factory uses state.
        byte[] body = Body(Var(2, 9_000_000_001), Msg(8, Body(Var(1, 7), Var(2, 2), Msg(5, owner))));
        var map = Assert.IsType<MapComplementaryInformationEvent>(_codec.Decode(CodecTests.Frame(1, "jss", body)).Content);
        var bag = Assert.IsType<MapSpecificComplementaryInformationCaseHavenBagInformation>(map.SpecificCase).Value;
        Assert.Equal(7, bag.Theme);
        Assert.Equal(2, bag.RoomId);
        var basic = Assert.IsType<CharacterComplementCaseCharacterBasicInformation>(bag.OwnerInformation.ComplementCase).Value;
        Assert.Equal("Owner", basic.Name);
        Assert.Equal(253, basic.Level);
        Assert.IsType<CharacterBasicInformationComplementCaseNone>(basic.ComplementCase);
        var expected = new MapComplementaryInformationEvent(9_000_000_001, [], 0, [], [], [], [], [], [],
            new MapSpecificComplementaryInformationCaseHavenBagInformation(new(7, 2, new(9_000_000_002,
                new CharacterComplementCaseCharacterBasicInformation(new("Owner", 253, new CharacterBasicInformationComplementCaseNone()))))));
        AssertEncoded("jss", body, expected);
    }

    private static ActorPositionInformation Actor(RolePlayActorCase identity)
        => new(new(312, Direction.NorthWest, null, null), new(Look, new ActorInformationCaseRolePlayActor(new(identity))), -9_000_000_003);
    private static byte[] ActorBytes(int kind, byte[] identity)
        => Body(Msg(1, Body(Var(1, 312), Var(2, 5))), Msg(2, Body(Msg(1, Msg(kind, identity)), Msg(3, LookBytes))), Var(3, -9_000_000_003));
    private ActorPositionInformation DecodeActor(byte[] actor)
        => Assert.Single(Assert.IsType<GameRolePlayShowActorsEvent>(_codec.Decode(CodecTests.Frame(1, "jsn", Msg(1, actor))).Content).Actors);
    private static RolePlayActor Roleplay(ActorPositionInformation actor)
        => Assert.IsType<ActorInformationCaseRolePlayActor>(actor.ActorInformation!.ActorCase).Value;
    private void AssertEncoded(string alias, byte[] expected, GameEvent message)
    {
        var envelope = _codec.ReadEnvelope(_codec.Encode(new(message)));
        Assert.Equal(alias, envelope.Alias);
        Assert.Null(envelope.Uid);
        Assert.Equal(GameMessageKind.Event, envelope.Kind);
        Assert.Equal(_codec.DecodeRawPayload(alias, expected), _codec.DecodeRawPayload(alias, envelope.Content.Value.Span));
    }
    private static byte[] Body(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
    private static byte[] Var(int tag, long value) => Write(output => { output.WriteTag(tag, WireFormat.WireType.Varint); output.WriteInt64(value); });
    private static byte[] Msg(int tag, byte[] value) => Write(output => { output.WriteTag(tag, WireFormat.WireType.LengthDelimited); output.WriteBytes(ByteString.CopyFrom(value)); });
    private static byte[] Text(int tag, string value) => Msg(tag, System.Text.Encoding.UTF8.GetBytes(value));
    private static byte[] Packed(int tag, long[] values) => Msg(tag, Write(output => { foreach (long value in values) output.WriteInt64(value); }));
    private static byte[] Write(Action<CodedOutputStream> write)
    {
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, leaveOpen: true)) write(output);
        return stream.ToArray();
    }
}
