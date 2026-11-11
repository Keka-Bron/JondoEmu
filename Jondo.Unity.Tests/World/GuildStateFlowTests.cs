using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World;

[Collection("guild raids")]
public sealed class GuildStateFlowTests : IDisposable
{
    private static readonly GameCodec Codec = new();
    private const long Leader = 8_195_001, Member = 8_195_002;
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"jondo-guild-state-{Guid.NewGuid():N}.db");
    public GuildStateFlowTests() => GuildStore.ConnectionStringOverride = $"Data Source={_file}";
    public void Dispose()
    {
        GuildStore.ConnectionStringOverride = null;
        TestDatabases.ReleasePool(_file);
        File.Delete(_file);
    }

    private static GuildStore.Guild Create(long leader = Leader) => GuildStore.Create(leader, "BeforeGuild", 101, 202, 0x123456, 0xabcdef);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Conditional_entry_uses_actual_membership_contributions_hall_and_header_after_ranks(bool guilded)
    {
        var guild = Create();
        var custom = GuildStore.CreateRank(guild.Id, "Custom", 91_001, 2);
        if (guilded) GuildStore.Join(Member, guild.Id, custom.Id);
        if (guilded) { GuildStore.Contribute(Member, guild.Id); GuildStore.Contribute(Member, guild.Id); }
        GuildHalls.Choose(guild.Id, 83_003, DateTimeOffset.UtcNow);
        GuildHalls.SetTheme(guild.Id, 83_099);
        var packets = new List<byte[]>();
        var session = new GameSession(Codec, packet => { packets.Add(packet); return Task.CompletedTask; });
        session.State.CharacterId = Member;
        using var wrongContext = SessionContext.Push(GameSession.SinSocket());
        using var turn = await session.EnterTurnAsync();
        await GuildHandler.SendMembershipAtEntryAsync(session).WaitAsync(TimeSpan.FromSeconds(5));
        if (!guilded) { Assert.Empty(packets); return; }
        Assert.Equal(4, packets.Count);
        Assert.IsType<GuildRanksEvent>(Codec.Decode(packets[0]).Content);
        var membership = Assert.IsType<GuildMembershipEvent>(Codec.Decode(packets[1]).Content);
        Assert.Equal(new GuildMembershipEvent(new GuildInformation(checked((int)guild.Id), "BeforeGuild", 1,
            new GuildSocialInformation(new SocialEmblem(101, 202, 0x123456, 0xabcdef))), custom.Id, 20, 0), membership);
        var header = Assert.IsType<GuildGeneralInformationEvent>(Codec.Decode(packets[2]).Content);
        Assert.Equal(83_003, header.HallId);
        Assert.Equal(20, header.GuildKamas);
        Assert.Equal(2, header.MembersCount);
        Assert.Equal(guild.FoundedUtc, header.CreationDate);
        Assert.Equal(0, header.Score);
        Assert.IsType<GuildMembersEvent>(Codec.Decode(packets[3]).Content);
        Assert.All(packets, packet => Assert.Null(Codec.ReadEnvelope(packet).Uid));
    }

    [Fact]
    public async Task Window_keeps_header_on_its_requested_tab_and_reloads_a_changed_hall()
    {
        var guild = Create();
        await using var pipe = await ClientPipe.OpenAsync(8_195_101, Leader, 8_195_201);
        using var context = SessionContext.Push(pipe.Session);
        await GuildHandler.OpenWindowAsync(pipe.ToClient);
        Assert.NotEqual(nameof(GuildGeneralInformationEvent), GameBindings.StableName(Codec.ReadEnvelope(await Packet(pipe)).Alias));
        await GuildHandler.TabAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Jiy, []));
        var first = Assert.IsType<GuildGeneralInformationEvent>(Codec.Decode(await Packet(pipe)).Content);
        Assert.Null(first.HallId);
        Assert.Equal(1, first.MembersCount);
        await Packet(pipe); // untouched contributions-left notification
        GuildHalls.Choose(guild.Id, 83_004, DateTimeOffset.UtcNow);
        GuildHalls.SetTheme(guild.Id, 83_099);
        await GuildHandler.TabAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Jiy, []));
        Assert.Equal(83_004, Assert.IsType<GuildGeneralInformationEvent>(Codec.Decode(await Packet(pipe)).Content).HallId);
        await Packet(pipe);
        await pipe.Quiet();
    }

    [Fact]
    public async Task Expulsion_refreshes_remaining_members_with_the_actual_member_count_after_the_member_list()
    {
        var guild = Create();
        GuildStore.Join(Member, guild.Id);
        await using var leader = await ClientPipe.OpenAsync(8_195_102, Leader, 8_195_202);
        await using var member = await ClientPipe.OpenAsync(8_195_103, Member, 8_195_203);
        using var context = SessionContext.Push(leader.Session);
        Assert.Null(await GuildHandler.KickAsync(Leader, Member));
        Assert.IsType<GuildMembersEvent>(Codec.Decode(await Packet(leader)).Content);
        var header = Assert.IsType<GuildGeneralInformationEvent>(Codec.Decode(await Packet(leader)).Content);
        Assert.Equal(1, header.MembersCount);
        Assert.Null(GuildStore.GuildOf(Member));
        await leader.Quiet();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rename_and_emblem_changes_send_each_members_own_rank_and_total_before_the_same_header(bool emblem)
    {
        if (!File.Exists(Paths.WorldDb)) return;
        long account = emblem ? 8_195_502 : 8_195_501;
        long who = Character(account, emblem ? "State-Emblem" : "State-Rename");
        try
        {
            var guild = Create(who);
            var custom = GuildStore.CreateRank(guild.Id, "Member", 91_001, 2);
            GuildStore.Join(Member, guild.Id, custom.Id);
            GuildStore.Contribute(who, guild.Id);
            GuildStore.Contribute(Member, guild.Id);
            GuildStore.Contribute(Member, guild.Id);
            GuildHalls.Choose(guild.Id, 83_005, DateTimeOffset.UtcNow);
            await using var leader = await ClientPipe.OpenAsync(account, who, 8_195_301);
            await using var member = await ClientPipe.OpenAsync(8_195_503, Member, 8_195_302);
            using var context = SessionContext.Push(leader.Session);
            using var turn = await leader.Session.EnterTurnAsync();
            int template = emblem ? GuildHandler.ReEmblemPotionTemplate : GuildHandler.RenamePotionTemplate;
            Assert.True(await Equipment.GiveAsync(leader.ToClient, template, 1));
            Assert.True(await GuildHandler.TryUsePotionAsync(leader.ToClient, Equipment.All.Single(item => item.Template == template)));
            await UntilLegacy(leader, Op.Jje);
            if (emblem)
                await GuildHandler.ReEmblemAsync(leader.ToClient, ConnectionProtocol.Push(Op.Jhz,
                    Pb.New().Msg(1, Pb.New().Var(1, 303).Var(2, 404).Var(3, 0x234567).Var(5, 0xbcdef0)).Build()));
            else await GuildHandler.RenameAsync(leader.ToClient, ConnectionProtocol.Push(Op.Jhv, Pb.New().Str(1, "AfterGuild").Build()));
            await UntilLegacy(leader, Op.Jhq);
            var ownMembership = Assert.IsType<GuildMembershipEvent>(Codec.Decode(await Packet(leader)).Content);
            var ownHeader = Assert.IsType<GuildGeneralInformationEvent>(Codec.Decode(await Packet(leader)).Content);
            var otherMembership = Assert.IsType<GuildMembershipEvent>(Codec.Decode(await Packet(member)).Content);
            var otherHeader = Assert.IsType<GuildGeneralInformationEvent>(Codec.Decode(await Packet(member)).Content);
            Assert.Equal(GuildStore.RankLeader, ownMembership.RankId);
            Assert.Equal(custom.Id, otherMembership.RankId);
            Assert.Equal(10, ownMembership.GuildTokens);
            Assert.Equal(20, otherMembership.GuildTokens);
            Assert.Equal(ownMembership.GuildInformation, otherMembership.GuildInformation);
            Assert.Equal(emblem ? "BeforeGuild" : "AfterGuild", ownMembership.GuildInformation!.Name);
            Assert.Equal(emblem ? new SocialEmblem(303, 404, 0x234567, 0xbcdef0) : new SocialEmblem(101, 202, 0x123456, 0xabcdef),
                ownMembership.GuildInformation.SocialInformation!.Emblem);
            Assert.Equal(ownHeader, otherHeader);
            Assert.Equal(83_005, ownHeader.HallId);
            Assert.Equal(2, ownHeader.MembersCount);
            Assert.Equal(30, ownHeader.GuildKamas);
        }
        finally { DatabaseManager.DeleteCharacter(who, account); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Contribution_and_mission_milestones_send_fresh_experience_after_the_reward_notifications(bool mission)
    {
        if (!File.Exists(Paths.WorldDb) || GuildMissions.All.Count == 0) return;
        long account = mission ? 8_195_505 : 8_195_504;
        long who = Character(account, mission ? "State-Mission" : "State-Contribution");
        try
        {
            var guild = Create(who);
            var now = DateTimeOffset.UtcNow;
            GuildActivity.Choose(guild.Id, 1, [], now);
            var selected = GuildMissions.All.First(candidate => candidate.ActivityPoints > 0 && candidate.ActivityPoints < 5000
                && candidate.Objectives.Count == 1 && GuildMissions.ObjectiveOf(candidate.Objectives[0]).MonsterId > 0);
            int points = mission ? selected.ActivityPoints : GuildActivity.ContributionActivityPoints;
            Assert.Equal(0, GuildActivity.Credit(guild, who, 0, 5000 - points, now).Experience);
            GuildHalls.Choose(guild.Id, 83_006, now);
            if (mission) SeedMission(guild.Id, GuildActivity.Of(guild.Id, now).Name, selected);
            await using var pipe = await ClientPipe.OpenAsync(account, who, 8_195_303);
            using var context = SessionContext.Push(pipe.Session);
            using var turn = await pipe.Session.EnterTurnAsync();
            if (mission)
                await GuildMissionWatcher.CountAsync(pipe.ToClient, guild, who,
                    new Dictionary<int, int> { [GuildMissions.ObjectiveOf(selected.Objectives[0]).MonsterId] = 1 }, pipe.Session.MapId, now);
            else
            {
                pipe.Session.State.Kamas = GuildStore.ContributionKamas * 2;
                await GuildHandler.ContributeAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Jlb, []));
            }
            var prefix = new List<byte[]>();
            GuildGeneralInformationEvent? header = null;
            for (int i = 0; i < 64 && header == null; i++)
            {
                var packet = await Packet(pipe);
                if (GameBindings.StableName(Codec.ReadEnvelope(packet).Alias) == nameof(GuildGeneralInformationEvent))
                    header = Assert.IsType<GuildGeneralInformationEvent>(Codec.Decode(packet).Content);
                else prefix.Add(packet);
            }
            Assert.NotNull(header);
            Assert.Equal(40, header.Experience);
            Assert.Equal(1, header.Level);
            Assert.Equal(0, header.ExpLevelFloor);
            Assert.Equal(50, header.ExpLevelNextFloor);
            Assert.Equal(83_006, header.HallId);
            Assert.Equal(1, header.MembersCount);
            Assert.Equal(0, header.Score);
            // These notifications are still legacy sends; pin only their ordering
            // before the new typed header, not the converted header's wire format.
            byte[] rewardNotice = mission
                ? ConnectionProtocol.Push(Op.Jen, GuildProtocol.BuildMissionDone(selected.Id))
                : ConnectionProtocol.Push(Op.Jle, GuildProtocol.BuildContribution(GuildStore.ContributionKamas, 4));
            Assert.Contains(prefix, packet => packet.AsSpan().SequenceEqual(rewardNotice));
            Assert.Equal(40, GuildStore.ById(guild.Id).Experience);
            await pipe.Quiet();
        }
        finally { DatabaseManager.DeleteCharacter(who, account); }
    }

    [Fact]
    public void Oversized_persisted_contribution_totals_fail_instead_of_wrapping()
    {
        var guild = Create();
        GuildStore.Contribute(Leader, guild.Id);
        using var connection = new SqliteConnection(GuildStore.ConnectionStringOverride);
        connection.Open();
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE GuildContributions SET Done = 214748365 WHERE CharacterId = $who;";
        update.Parameters.AddWithValue("$who", Leader);
        update.ExecuteNonQuery();
        Assert.Throws<OverflowException>(() => GuildStateService.Live.Membership(guild, 1, Leader));
    }

    [Fact]
    public async Task Connection_warning_uses_the_recipients_codec_after_the_roster_update_and_is_login_opt_in()
    {
        if (!File.Exists(Paths.WorldDb)) return;
        const long account = 8_195_508;
        long who = Character(account, "Presence-Owner");
        try
        {
            var guild = Create(who);
            GuildStore.Join(Member, guild.Id);
            await using var own = await ClientPipe.OpenAsync(account, who, 8_195_218);
            var codec = new PresenceCodec();
            await using var other = await ClientPipe.OpenAsync(8_195_108, Member, 8_195_219, codec: codec);
            using var context = SessionContext.Push(GameSession.SinSocket());
            using var turn = await own.Session.EnterTurnAsync();
            other.Session.State.WarnGuildConnections = true;
            await GuildHandler.TellConnectionAsync(who, online: true).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(ConnectionProtocol.ReadPayload(await Packet(other), Op.Jgz));
            var frame = Codec.Decode(await Packet(other));
            Assert.Null(frame.Uid);
            Assert.Equal(new GuildMemberOnlineStatusEvent(who, true), frame.Content);
            Assert.Single(codec.Frames);
            other.Session.State.WarnGuildConnections = false;
            await GuildHandler.TellConnectionAsync(who, online: true);
            Assert.NotNull(ConnectionProtocol.ReadPayload(await Packet(other), Op.Jgz));
            other.Session.State.WarnGuildConnections = true;
            await GuildHandler.TellConnectionAsync(who, online: false);
            Assert.NotNull(ConnectionProtocol.ReadPayload(await Packet(other), Op.Jgz));
            Assert.Single(codec.Frames);
            await own.Quiet();
            await other.Quiet();
        }
        finally { DatabaseManager.DeleteCharacter(who, account); }
    }

    private sealed class PresenceCodec : IGameCodec
    {
        internal readonly List<GameFrame> Frames = [];
        public byte[] Encode(GameFrame frame) { Frames.Add(frame); return Codec.Encode(frame); }
        public GameFrame Decode(ReadOnlySpan<byte> packet) => Codec.Decode(packet);
    }

    private static long Character(long account, string name) => DatabaseManager.CreateCharacter(account, 1, name, 4, 0, 1,
        new long[] { -1, -1, -1, -1, -1 }, 191105026, 200, 0, 0, Array.Empty<(int, int)>());
    private static Task<byte[]> Packet(ClientPipe pipe) => Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer).WaitAsync(TimeSpan.FromSeconds(5));
    private static async Task UntilLegacy(ClientPipe pipe, string opcode)
    {
        for (int i = 0; i < 32; i++) if (ConnectionProtocol.ReadPayload(await Packet(pipe), opcode) != null) return;
        throw new Xunit.Sdk.XunitException($"Legacy notification {opcode} did not arrive.");
    }

    private static void SeedMission(long guildId, string week, GuildMissions.Mission mission)
    {
        using var connection = new SqliteConnection(GuildStore.ConnectionStringOverride);
        connection.Open();
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO GuildMissionDraws (GuildId, Week, Slot, MissionId, Uuid, ObjectiveId) VALUES ($g, $w, 0, $m, $u, $o);";
        insert.Parameters.AddWithValue("$g", guildId);
        insert.Parameters.AddWithValue("$w", week);
        insert.Parameters.AddWithValue("$m", mission.Id);
        insert.Parameters.AddWithValue("$u", Guid.NewGuid().ToString());
        insert.Parameters.AddWithValue("$o", mission.Objectives[0]);
        insert.ExecuteNonQuery();
    }
}
