using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf;
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
public sealed class GuildRankFlowTests : IDisposable
{
    private static readonly GameCodec Codec = new();
    private const long Leader = 8_194_001, Member = 8_194_002;
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"jondo-ranks-{Guid.NewGuid():N}.db");

    public GuildRankFlowTests() => GuildStore.ConnectionStringOverride = $"Data Source={_file}";

    public void Dispose()
    {
        GuildStore.ConnectionStringOverride = null;
        TestDatabases.ReleasePool(_file);
        File.Delete(_file);
    }

    private static GuildStore.Guild CreateGuild() => GuildStore.Create(Leader, "Rank snapshots", 165, 8, 16744448, 9476018);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Entry_uses_actual_ranks_at_both_positions_before_silent_membership_or_the_unguilded_template(bool guilded)
    {
        var guild = CreateGuild();
        var custom = GuildStore.CreateRank(guild.Id, "Custom member", 91_001, 2);
        if (guilded) GuildStore.Join(Member, guild.Id, custom.Id);
        var packets = new List<byte[]>();
        var session = new GameSession(Codec, packet => { packets.Add(packet); return Task.CompletedTask; });
        session.State.CharacterId = Member;
        var initial = new GameEntryInitialMessages([], [], session.State, new([], _ => []), 0, 0,
            () => new([]), () => new("1970-01-01T00:00:00.000Z", []), () => GuildRankService.Live.Initial(session.CharacterId),
            () => CharacterSelectionMessages.Success(new() { Id = Member, Name = "Selected", Level = 1 }, new([], 3, 1, [], [], [])),
            () => InventoryMessages.FromCatalogue.Snapshot(session.State), () => new([]), () => new(81_001),
            () => EntryInformationMessages.PreviousConnection(session.State.PreviousVisit),
            new(_ => 0, _ => [], _ => 0, ItemEffectMessages.FromCatalogue),
            new(() => new(0), () => new(null), () => new(null), () => new("", null)));
        using var wrongContext = SessionContext.Push(GameSession.SinSocket());
        using var turn = await session.EnterTurnAsync();
        await initial.SendAsync(session);
        int membershipStart = packets.Count;
        await GuildHandler.SendMembershipAtEntryAsync(session).WaitAsync(TimeSpan.FromSeconds(5));
        var snapshots = packets.Where(IsRanks).Select(packet => Assert.IsType<GuildRanksEvent>(Codec.Decode(packet).Content)).ToArray();
        Assert.Equal(guilded ? 2 : 1, snapshots.Length);
        Assert.All(snapshots, snapshot =>
        {
            if (guilded) Assert.Equal("Custom member", Assert.Single(snapshot.Ranks, rank => rank.Id == custom.Id).Name);
            else Assert.Equal(new[] { 1, 2, 3, 4 }, snapshot.Ranks.Select(rank => rank.Id));
        });
        var membership = packets.Skip(membershipStart).Select(packet => Codec.Decode(packet).Content).ToArray();
        if (guilded) Assert.Collection(membership,
            message => Assert.IsType<GuildRanksEvent>(message),
            message => Assert.Equal(custom.Id, Assert.IsType<GuildMembershipEvent>(message).RankId),
            message => Assert.IsType<GuildGeneralInformationEvent>(message),
            message => Assert.IsType<GuildMembersEvent>(message));
        else Assert.Empty(membership);
    }

    [Fact]
    public async Task Direct_join_sends_actual_custom_ranks_before_joined_and_member_state()
    {
        var guild = CreateGuild();
        var custom = GuildStore.CreateRank(guild.Id, "Custom member", 91_001, 2);
        GuildStore.Join(Member, guild.Id, custom.Id);
        await using var pipe = await ClientPipe.OpenAsync(8_194_101, Member, 8_194_201);
        using var context = SessionContext.Push(GameSession.SinSocket());
        using var turn = await pipe.Session.EnterTurnAsync();
        await GuildHandler.SendGuildToOwnerAsync(pipe.Session, guild).WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = await NextRanksAsync(pipe);
        Assert.Equal("Custom member", Assert.Single(snapshot.Ranks, rank => rank.Id == custom.Id).Name);
        // Joining remains a legacy packet; check its position without decoding it back
        // into a semantic rank snapshot or using its fields as server expectations.
        Assert.Equal(ConnectionProtocol.Push(Op.Jgw, GuildProtocol.BuildGuildJoined(guild, custom.Id)), await PacketAsync(pipe));
        Assert.IsType<GuildMembersEvent>(Codec.Decode(await PacketAsync(pipe)).Content);
        Assert.IsType<GuildGeneralInformationEvent>(Codec.Decode(await PacketAsync(pipe)).Content);
        await pipe.Quiet();
    }

    [Fact]
    public async Task Accepted_application_sends_the_recipient_custom_ranks_instead_of_the_leaders_codec_or_defaults()
    {
        var guild = CreateGuild();
        var newcomer = GuildStore.Ranks(guild.Id).Single(rank => rank.Id == GuildStore.RankNewcomer);
        newcomer.Name = "Recrue personnalisée";
        newcomer.Icon = 91_002;
        newcomer.Rights = [128, 1]; // integer 128, not permission 1
        GuildStore.SaveRank(newcomer);
        var extra = GuildStore.CreateRank(guild.Id, "Another custom rank", 91_003, 2);
        GuildStore.Apply(Member, guild.Id, "Ready");
        var leaderPackets = new List<byte[]>();
        var memberPackets = new List<byte[]>();
        var leaderCodec = new RecordingCodec();
        var memberCodec = new RecordingCodec();
        var leader = Registered(Leader, 8_194_102, leaderPackets, leaderCodec);
        var member = Registered(Member, 8_194_103, memberPackets, memberCodec);
        try
        {
            using var context = SessionContext.Push(leader);
            using var turn = await leader.EnterTurnAsync();
            await GuildHandler.AnswerApplicationAsync(null!, ConnectionProtocol.Push(Op.Jlt,
                Pb.New().Var(1, 1).Var(2, Member).Build())).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(leader, SessionContext.Actual);
            var snapshot = Assert.IsType<GuildRanksEvent>(Codec.Decode(memberPackets[1]).Content);
            var received = Assert.Single(snapshot.Ranks, rank => rank.Id == GuildStore.RankNewcomer);
            Assert.Equal("Recrue personnalisée", received.Name);
            Assert.Equal(91_002, received.OrderAndGfx!.GfxId);
            Assert.Equal(new[] { 128 }, received.PrivateInformation!.Rights);
            Assert.Contains(snapshot.Ranks, rank => rank.Id == extra.Id);
            Assert.Equal(ConnectionProtocol.Push(Op.Jgw, GuildProtocol.BuildGuildJoined(guild, GuildStore.RankNewcomer)), memberPackets[2]);
            Assert.DoesNotContain(leaderPackets, IsRanks);
            Assert.Single(memberPackets, IsRanks);
            Assert.IsType<GuildGeneralInformationEvent>(Assert.Single(leaderCodec.Encoded).Content);
            Assert.Collection(memberCodec.Encoded,
                frame => Assert.IsType<GuildRanksEvent>(frame.Content),
                frame => Assert.IsType<GuildGeneralInformationEvent>(frame.Content));
            Assert.Equal(guild.Id, GuildStore.GuildOf(Member)!.Id);
        }
        finally
        {
            SessionRegistry.Unregister(leader);
            SessionRegistry.Unregister(member);
        }
    }

    [Fact]
    public async Task Rank_management_refreshes_persisted_edits_reordering_rights_creation_and_deletion()
    {
        var guild = CreateGuild();
        var custom = GuildStore.CreateRank(guild.Id, "Original", 91_001, 2);
        await using var pipe = await ClientPipe.OpenAsync(8_194_104, Leader, 8_194_204);
        using var context = SessionContext.Push(pipe.Session);
        using var turn = await pipe.Session.EnterTurnAsync();
        await GuildHandler.RanksAsync(pipe.ToClient, []);
        Assert.Equal("Original", Rank(await NextRanksAsync(pipe), custom.Id).Name);

        await GuildHandler.EditRankAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Jct,
            Pb.New().Msg(1, Pb.New().Str(2, "Edited").Msg(4, Pb.New()).Var(5, custom.Id)).Build()));
        var edited = Rank(await NextRanksAsync(pipe), custom.Id);
        Assert.Equal("Edited", edited.Name);
        Assert.Equal(91_001, edited.OrderAndGfx!.GfxId); // captured empty graphic block retains the icon

        await GuildHandler.UpdateRanksAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Jcu,
            Pb.New().Msg(1, Pb.New().Msg(4, Pb.New().Var(2, 91_005).Var(3, 0)).Var(5, custom.Id)).Build()));
        Assert.Equal(new RankInformationOrderAndGfx(91_005, 0), Rank(await NextRanksAsync(pipe), custom.Id).OrderAndGfx);

        await GuildHandler.SetRightsAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Jck,
            Pb.New().Packed(1, new long[] { 128, 257, 128 }).Var(2, custom.Id).Build()));
        var permissions = Rank(await NextRanksAsync(pipe), custom.Id).PrivateInformation!;
        Assert.True(permissions.Modifiable);
        Assert.Equal(new[] { 128, 257, 128 }, permissions.Rights);

        await GuildHandler.CreateRankAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Jcv,
            Pb.New().Var(1, 2).Str(4, "Created").Var(5, 91_006).Build()));
        var created = Assert.Single((await NextRanksAsync(pipe)).Ranks, rank => rank.Name == "Created");
        Assert.Equal(new RankInformationOrderAndGfx(91_006, 2), created.OrderAndGfx);
        Assert.Empty(created.PrivateInformation!.Rights);

        await GuildHandler.DeleteRankAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Jcn,
            Pb.New().Var(1, created.Id).Var(2, GuildStore.RankNewcomer).Build()));
        Assert.DoesNotContain((await NextRanksAsync(pipe)).Ranks, rank => rank.Id == created.Id);
        await pipe.Quiet();
    }

    [Fact]
    public void Stored_permission_checks_use_full_integer_ids_and_invalid_writes_leave_previous_rights()
    {
        var guild = CreateGuild();
        var custom = GuildStore.CreateRank(guild.Id, "Permissions", 91_001, 2);
        custom.Rights = [128, 1, 129, 2]; // 128, 257
        GuildStore.SaveRank(custom);
        GuildStore.Join(Member, guild.Id, custom.Id);
        Assert.True(GuildStore.HasRight(Member, 128));
        Assert.True(GuildStore.HasRight(Member, 257));
        Assert.False(GuildStore.HasRight(Member, 1));
        Assert.False(GuildStore.HasRight(Member, 2));
        Assert.True(GuildStore.HasRight(Leader, 1));
        Assert.Equal(new[] { 128, 257 }, GuildChests.RightsOf(Member).Order());
        custom.Rights = [8, 128];
        Assert.Throws<InvalidProtocolBufferException>(() => GuildStore.SaveRank(custom));
        Assert.Equal(new[] { 128, 257 }, Rank(GuildRankService.Live.Snapshot(guild.Id), custom.Id).PrivateInformation!.Rights);
    }

    [Fact]
    public void Older_guilds_without_saved_ranks_use_and_persist_the_server_default_template()
    {
        var guild = CreateGuild();
        using var connection = new SqliteConnection(GuildStore.ConnectionStringOverride);
        connection.Open();
        using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM GuildRanks WHERE GuildId = $guild;";
        delete.Parameters.AddWithValue("$guild", guild.Id);
        delete.ExecuteNonQuery();
        var snapshot = GuildRankService.Live.Snapshot(guild.Id);
        Assert.Equal(new[] { 1, 2, 3, 4 }, snapshot.Ranks.Select(rank => rank.Id));
        Assert.Equal(new[] { "guild.rank.1.name", "guild.rank.2.name", "guild.rank.3.name", "guild.rank.4.name" }, snapshot.Ranks.Select(rank => rank.Name));
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM GuildRanks WHERE GuildId = $guild;";
        count.Parameters.AddWithValue("$guild", guild.Id);
        Assert.Equal(4L, count.ExecuteScalar());
    }

    [Fact]
    public async Task Failed_rank_write_does_not_emit_a_refresh_or_change_saved_rights()
    {
        var guild = CreateGuild();
        var custom = GuildStore.CreateRank(guild.Id, "Permissions", 91_001, 2);
        using var connection = new SqliteConnection(GuildStore.ConnectionStringOverride);
        connection.Open();
        using var trigger = connection.CreateCommand();
        trigger.CommandText = "CREATE TRIGGER RejectRankWrite BEFORE UPDATE ON GuildRanks BEGIN SELECT RAISE(ABORT, 'rank write failure'); END;";
        trigger.ExecuteNonQuery();
        await using var pipe = await ClientPipe.OpenAsync(8_194_105, Leader, 8_194_205);
        using var context = SessionContext.Push(pipe.Session);
        await Assert.ThrowsAsync<SqliteException>(() => GuildHandler.SetRightsAsync(pipe.ToClient,
            ConnectionProtocol.Push(Op.Jck, Pb.New().Packed(1, new long[] { 128, 257 }).Var(2, custom.Id).Build())));
        Assert.Empty(Rank(GuildRankService.Live.Snapshot(guild.Id), custom.Id).PrivateInformation!.Rights);
        await pipe.Quiet();
    }

    private static RankInformation Rank(GuildRanksEvent snapshot, int id) => Assert.Single(snapshot.Ranks, rank => rank.Id == id);
    private static bool IsRanks(byte[] packet) => GameBindings.StableName(Codec.ReadEnvelope(packet).Alias) == nameof(GuildRanksEvent);
    private static Task<byte[]> PacketAsync(ClientPipe pipe)
        => Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer).WaitAsync(TimeSpan.FromSeconds(5));
    private static async Task<GuildRanksEvent> NextRanksAsync(ClientPipe pipe)
    {
        var frame = Codec.Decode(await PacketAsync(pipe));
        Assert.Null(frame.Uid);
        return Assert.IsType<GuildRanksEvent>(frame.Content);
    }

    private static GameSession Registered(long characterId, long accountId, List<byte[]> packets, IGameCodec codec)
    {
        var session = new GameSession(codec, packet => { packets.Add(packet); return Task.CompletedTask; });
        session.BindAccount(accountId, 1);
        session.State.CharacterId = characterId;
        session.State.MapId = 8_194_999 + characterId;
        session.EnterWorld();
        Assert.True(SessionRegistry.Register(session));
        return session;
    }

    private sealed class RecordingCodec : IGameCodec
    {
        internal readonly List<GameFrame> Encoded = [];
        public byte[] Encode(GameFrame frame)
        {
            Encoded.Add(frame);
            return Codec.Encode(frame);
        }
        public GameFrame Decode(ReadOnlySpan<byte> frame) => throw new InvalidOperationException("No decoding expected.");
    }
}
