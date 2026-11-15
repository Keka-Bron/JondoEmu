using Google.Protobuf;
using Jondo.Unity.Protocol.Game.Wire;
using Xunit;

namespace Jondo.Unity.Protocol.Game.Tests;

public sealed class MapFightContractTests
{
    private readonly GameCodec _codec = new();

    [Fact]
    public void Captured_swords_keep_flags_teams_and_present_empty_defenders_and_options()
    {
        const long leader = 52000000009;
        var attackers = MakeTeam(TeamType.Player, leader, Team.Challenger,
            [new(leader, new FightTeamMemberInformationCaseCharacter(new(201, "Fighter1")))]);
        var defenders = MakeTeam(TeamType.Monster, -21200, Team.Defender, []);
        byte[] captured = Convert.FromHexString(
            "0a490a04b401b60110041a23188990c4dbc10120012a180a160a0d08c901120846696768746572" +
            "31108990c4dbc1011a13100118b0dafeffffffffffff0120012a0030012a002a0030831a");
        AssertEncoded("hpy", captured, new ShowChallengeEvent(new(3331, FightType.PvM,
            [attackers, defenders], [180, 182], [new(false, false, false, false), new(false, false, false, false)])));
        var decoded = Assert.IsType<ShowChallengeEvent>(_codec.Decode(CodecTests.Frame(1, "hpy", captured)).Content).FightInformation;
        Assert.Equal(new[] { 180, 182 }, decoded.TeamsPosition);
        Assert.Empty(Assert.IsType<FightTeamInformationCaseMembers>(decoded.TeamsInformation[1].TeamInformationCase).Value.Members);
    }

    [Fact]
    public void Growing_monster_team_and_collector_names_have_independent_populated_fixtures()
    {
        AssertEncoded("kae", Convert.FromHexString(
            "0a27100118dee3feffffffffffff0120012a140a1210ffffffffffffffffff012a0508ec031001300110e703"),
            new FightTeamUpdateEvent(MakeTeam(TeamType.Monster, -20002, Team.Defender,
                [new(-1, new FightTeamMemberInformationCaseMonsterMember(new(492, 1)))]), 487));
        byte[] collectorTeam = Body(Var(2, 3), Var(3, -20074), Var(4, 1),
            Msg(5, Msg(1, Body(Var(2, -1), Msg(3, Body(Var(1, 35), Var(2, 109)))))), Var(6, 1));
        AssertEncoded("kae", Body(Msg(1, collectorTeam), Var(2, int.MaxValue)),
            new FightTeamUpdateEvent(MakeTeam(TeamType.TaxCollector, -20074, Team.Defender,
                [new(-1, new FightTeamMemberInformationCaseTaxCollectorMember(new(35, 109)))]), int.MaxValue));
    }

    [Fact]
    public void Counts_hidden_flags_and_member_removals_keep_zero_signed_and_boundary_values()
    {
        AssertEncoded("jqz", [], new MapFightCountEvent(0));
        AssertEncoded("jqz", [0x10, 0x03], new MapFightCountEvent(3));
        AssertEncoded("hpr", Convert.FromHexString("089022"), new RemoveChallengeEvent(4368));
        AssertEncoded("hpr", Var(1, int.MaxValue), new RemoveChallengeEvent(int.MaxValue));
        AssertEncoded("jzw", Convert.FromHexString("08d703100120ffffffffffffffffff01"),
            new FightTeamRemoveMemberEvent(471, Team.Defender, -1));
        AssertEncoded("jzw", Body(Var(1, int.MaxValue), Var(4, long.MinValue)),
            new FightTeamRemoveMemberEvent(int.MaxValue, Team.Challenger, long.MinValue));
        AssertEncoded("kmu", Var(2, -9_000_000_001), new ContextRemoveElementEvent(-9_000_000_001));
    }

    [Theory]
    [InlineData(FightOption.Unknown0, 0)]
    [InlineData(FightOption.Unknown1, 1)]
    [InlineData(FightOption.Unknown2, 2)]
    [InlineData(FightOption.Unknown3, 3)]
    public void Option_updates_preserve_the_selector_state_team_and_fight(FightOption option, int number)
    {
        AssertEncoded("kau", Body(Var(1, 1), number == 0 ? [] : Var(3, number), Var(4, 1), Var(5, 487)),
            new FightOptionUpdateEvent(Team.Defender, option, true, 487));
        AssertEncoded("kau", Var(5, 487), new FightOptionUpdateEvent(Team.Challenger, FightOption.Unknown0, false, 487));
    }

    [Fact]
    public void Reconnection_notice_and_empty_modifier_keep_their_existing_contracts()
    {
        AssertEncoded("lqn", Convert.FromHexString("080110b801220c4865726f2d4578616d706c65"),
            new TextInformationEvent(TextInformationType.TextInformationError, 184, ["Hero-Example"]));
        AssertEncoded("lva", [], new AreaFightModifierUpdateEvent([]));
    }

    private static FightTeamInformation MakeTeam(TeamType type, long leader, Team team, IReadOnlyList<FightTeamMemberInformation> members)
        => new(type, leader, Alignment.Without, team, 0, new FightTeamInformationCaseMembers(new(members)));

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
    private static byte[] Write(Action<CodedOutputStream> write)
    {
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, leaveOpen: true)) write(output);
        return stream.ToArray();
    }
}
