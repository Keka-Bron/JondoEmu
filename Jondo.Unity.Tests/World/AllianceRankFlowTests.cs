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

[Collection("alliances")]
public sealed class AllianceRankFlowTests : IDisposable
{
    private static readonly GameCodec Codec = new();
    private const long Leader = 8_196_001, Member = 8_196_002;
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"jondo-alliance-ranks-{Guid.NewGuid():N}.db");
    public AllianceRankFlowTests() => AllianceStore.ConnectionStringOverride = $"Data Source={_file}";
    public void Dispose()
    {
        AllianceStore.ConnectionStringOverride = null;
        TestDatabases.ReleasePool(_file);
        File.Delete(_file);
    }
    private static AllianceStore.Alliance Create() => AllianceStore.Create(Leader, "Rank snapshots", "RKS", 101, 202, 0x123456, 0, 0xabcdef);

    [Fact]
    public async Task Entry_sends_actual_custom_ranks_before_membership_using_the_owner_under_another_context()
    {
        var alliance = Create();
        var custom = AllianceStore.CreateRank(alliance.Id, "Gardien", 91_001, 2);
        AllianceStore.Join(Member, alliance.Id, custom.Id);
        var spy = new RecordingCodec();
        await using var pipe = await ClientPipe.OpenAsync(8_196_101, Member, 8_196_201, codec: spy);
        var wrong = GameSession.SinSocket();
        using (SessionContext.Push(wrong))
        using (await pipe.Session.EnterTurnAsync())
        {
            await AllianceHandler.OnWorldEntryAsync(pipe.Session).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(wrong, SessionContext.Actual);
        }
        Assert.Equal("Gardien", Rank(await NextRanks(pipe), custom.Id).Name);
        // Membership precedes the typed notices; rank expectations remain semantic.
        Assert.Equal(ConnectionProtocol.Push(Op.Mbz, AllianceProtocol.BuildMembership(alliance, custom.Id)), await Packet(pipe));
        await Packet(pipe);
        await Packet(pipe);
        Assert.Collection(spy.Encoded,
            frame => Assert.IsType<AllianceRanksEvent>(frame.Content),
            frame => Assert.IsType<AllianceMotdEvent>(frame.Content),
            frame => Assert.IsType<AllianceBulletinEvent>(frame.Content));
        await pipe.Quiet();
    }

    [Fact]
    public async Task Joining_uses_the_owning_session_and_sends_ranks_before_its_existing_join_sequence()
    {
        var alliance = Create();
        var custom = AllianceStore.CreateRank(alliance.Id, "Gardien", 91_001, 2);
        AllianceStore.Join(Member, alliance.Id, custom.Id);
        var spy = new RecordingCodec();
        await using var pipe = await ClientPipe.OpenAsync(8_196_102, Member, 8_196_202, codec: spy);
        var wrong = GameSession.SinSocket();
        using (SessionContext.Push(wrong))
        using (await pipe.Session.EnterTurnAsync())
        {
            await AllianceHandler.SendJoinedAsync(pipe.Session, alliance).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(wrong, SessionContext.Actual);
        }
        Assert.Equal("Gardien", Rank(await NextRanks(pipe), custom.Id).Name);
        Assert.Equal(ConnectionProtocol.Push(Op.Mbz, AllianceProtocol.BuildMembership(alliance, custom.Id)), await Packet(pipe));
        await Packet(pipe); // information
        Assert.Equal(ConnectionProtocol.Push(Op.Mca, AllianceProtocol.BuildJoined(alliance, custom.Id)), await Packet(pipe));
        await Packet(pipe); // banner
        await Packet(pipe); // message
        await Packet(pipe); // bulletin; invented character has no actor to redraw
        Assert.Collection(spy.Encoded,
            frame => Assert.IsType<AllianceRanksEvent>(frame.Content),
            frame => Assert.IsType<AllianceMotdEvent>(frame.Content),
            frame => Assert.IsType<AllianceBulletinEvent>(frame.Content));
        await pipe.Quiet();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ungrouped_entry_omits_ranks_and_restores_only_an_existing_application(bool application)
    {
        if (!File.Exists(Paths.WorldDb)) return;
        long account = application ? 8_196_502 : 8_196_501;
        long who = Character(account, application ? "Rank-Applicant" : "Rank-Ungrouped");
        try
        {
            var alliance = Create();
            if (application) AllianceStore.Apply(who, alliance.Id, "Ready");
            var spy = new RecordingCodec();
            await using var pipe = await ClientPipe.OpenAsync(account, who, 8_196_203, codec: spy);
            using var context = SessionContext.Push(GameSession.SinSocket());
            await AllianceHandler.OnWorldEntryAsync(pipe.Session);
            Assert.Empty(spy.Encoded);
            if (application) Assert.NotNull(ConnectionProtocol.ReadPayload(await Packet(pipe), Op.Lzx));
            await pipe.Quiet();
        }
        finally { DatabaseManager.DeleteCharacter(who, account); }
    }

    [Fact]
    public void Missing_stored_ranks_use_catalogue_defaults_without_persisting_them()
    {
        var alliance = Create();
        using var connection = new SqliteConnection(AllianceStore.ConnectionStringOverride);
        connection.Open();
        using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM AllianceRanks WHERE AllianceId = $id;";
        delete.Parameters.AddWithValue("$id", alliance.Id);
        delete.ExecuteNonQuery();
        var ranks = AllianceRankService.Live.Snapshot(alliance.Id).Ranks;
        Assert.Equal(new[] { 1, 2, 3, 4 }, ranks.Select(rank => rank.Id));
        Assert.Equal(new[] { "alliance.rank.1.name", "alliance.rank.2.name", "alliance.rank.3.name", "alliance.rank.4.name" }, ranks.Select(rank => rank.Name));
        Assert.Equal(new[] { 116, 115, 114, 117 }, ranks.Select(rank => rank.OrderAndGfx!.GfxId));
        Assert.Equal(new[] { false, true, true, false }, ranks.Select(rank => rank.PrivateInformation!.Modifiable));
        Assert.Equal(new[] { 1, 2, 3, 4, 6, 7, 8, 9, 10, 11, 12, 13, 15, 16, 17, 18, 19, 21 }, ranks[0].PrivateInformation!.Rights);
        Assert.All(ranks.Skip(1), rank => Assert.Empty(rank.PrivateInformation!.Rights));
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM AllianceRanks WHERE AllianceId = $id;";
        count.Parameters.AddWithValue("$id", alliance.Id);
        Assert.Equal(0L, count.ExecuteScalar());
    }

    [Fact]
    public async Task Management_refreshes_persisted_mutations_to_every_recipient_and_removes_ranks_before_reassignment()
    {
        var alliance = Create();
        AllianceStore.Join(Member, alliance.Id, 3);
        var ownCodec = new RecordingCodec();
        var memberCodec = new RecordingCodec();
        await using var own = await ClientPipe.OpenAsync(8_196_103, Leader, 8_196_204, codec: ownCodec);
        await using var member = await ClientPipe.OpenAsync(8_196_104, Member, 8_196_205, codec: memberCodec);
        using var context = SessionContext.Push(own.Session);
        using var turn = await own.Session.EnterTurnAsync();
        await AllianceHandler.RanksAsync(own.ToClient);
        Assert.Equal(new[] { 1, 2, 3, 4 }, (await NextRanks(own)).Ranks.Select(rank => rank.Id));
        Assert.Empty(memberCodec.Encoded);

        await AllianceHandler.CreateRankAsync(own.ToClient, ConnectionProtocol.Push(Op.Mbj,
            Pb.New().Var(1, 91_001).Str(3, "Gardien").Var(4, 2).Build()));
        var created = Assert.Single((await NextRanks(own)).Ranks, rank => rank.Name == "Gardien");
        Assert.Equal(new[] { 1, 2, created.Id, 3, 4 }, (await NextRanks(member)).Ranks.Select(rank => rank.Id));

        await AllianceHandler.EditRankAsync(own.ToClient, ConnectionProtocol.Push(Op.Mbd,
            Pb.New().Msg(1, Pb.New().Str(2, "Éclaireur").Msg(4, Pb.New().Var(2, 91_002)).Var(5, created.Id)).Build()));
        foreach (var target in new[] { own, member })
        {
            var rank = Rank(await NextRanks(target), created.Id);
            Assert.Equal("Éclaireur", rank.Name);
            Assert.Equal(91_002, rank.OrderAndGfx!.GfxId);
            Assert.True(rank.PrivateInformation!.Modifiable);
        }

        await AllianceHandler.SetRightsAsync(own.ToClient, ConnectionProtocol.Push(Op.Mbl,
            Pb.New().Packed(2, new long[] { 3, 18, 3, int.MaxValue }).Var(3, created.Id).Build()));
        foreach (var target in new[] { own, member })
            Assert.Equal(new[] { 3, 18 }, Rank(await NextRanks(target), created.Id).PrivateInformation!.Rights);

        await AllianceHandler.ReorderRanksAsync(own.ToClient, ConnectionProtocol.Push(Op.Mbn,
            Pb.New().Msg(1, Pb.New().Str(2, "Éclaireur").Msg(4, Pb.New().Var(2, 91_002).Var(3, 99)).Var(5, created.Id)).Build()));
        foreach (var target in new[] { own, member })
        {
            var snapshot = await NextRanks(target);
            Assert.Equal(new[] { 1, 2, 3, created.Id, 4 }, snapshot.Ranks.Select(rank => rank.Id));
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, snapshot.Ranks.Select(rank => rank.OrderAndGfx!.Order));
        }

        AllianceStore.SetRank(Member, created.Id);
        await AllianceHandler.RemoveRankAsync(own.ToClient, ConnectionProtocol.Push(Op.Mbk,
            Pb.New().Var(1, 2).Var(2, created.Id).Build()));
        foreach (var target in new[] { own, member }) Assert.DoesNotContain((await NextRanks(target)).Ranks, rank => rank.Id == created.Id);
        Assert.Equal(ConnectionProtocol.Push(Op.Mbz, AllianceProtocol.BuildMembership(alliance, 2)), await Packet(member));
        Assert.Equal(2, AllianceStore.RankOf(Member));
        Assert.Equal(6, ownCodec.Encoded.Count);
        Assert.Equal(5, memberCodec.Encoded.Count);
        Assert.All(ownCodec.Encoded.Concat(memberCodec.Encoded), frame => Assert.IsType<AllianceRanksEvent>(frame.Content));
        await own.Quiet();
        await member.Quiet();
    }

    [Fact]
    public async Task Permission_refusal_does_not_write_or_broadcast_a_successful_rank_refresh()
    {
        var alliance = Create();
        AllianceStore.Join(Member, alliance.Id, 3);
        var spy = new RecordingCodec();
        await using var member = await ClientPipe.OpenAsync(8_196_105, Member, 8_196_206, codec: spy);
        using var context = SessionContext.Push(member.Session);
        await AllianceHandler.EditRankAsync(member.ToClient, ConnectionProtocol.Push(Op.Mbd,
            Pb.New().Msg(1, Pb.New().Str(2, "Forbidden").Msg(4, Pb.New().Var(2, 91_002)).Var(5, 2)).Build()));
        Assert.NotNull(ConnectionProtocol.ReadPayload(await Packet(member), Op.Lqn));
        Assert.Empty(spy.Encoded);
        Assert.Equal("alliance.rank.2.name", Rank(AllianceRankService.Live.Snapshot(alliance.Id), 2).Name);
        await member.Quiet();
    }

    [Fact]
    public async Task Failed_storage_write_does_not_broadcast_or_change_saved_rights()
    {
        var alliance = Create();
        var custom = AllianceStore.CreateRank(alliance.Id, "Gardien", 91_001, 2);
        using var connection = new SqliteConnection(AllianceStore.ConnectionStringOverride);
        connection.Open();
        using var trigger = connection.CreateCommand();
        trigger.CommandText = "CREATE TRIGGER RejectAllianceRankWrite BEFORE INSERT ON AllianceRanks BEGIN SELECT RAISE(ABORT, 'rank write failure'); END;";
        trigger.ExecuteNonQuery();
        var spy = new RecordingCodec();
        await using var own = await ClientPipe.OpenAsync(8_196_106, Leader, 8_196_207, codec: spy);
        using var context = SessionContext.Push(own.Session);
        await Assert.ThrowsAsync<SqliteException>(() => AllianceHandler.SetRightsAsync(own.ToClient,
            ConnectionProtocol.Push(Op.Mbl, Pb.New().Packed(2, new long[] { 3, 18 }).Var(3, custom.Id).Build())));
        Assert.Empty(spy.Encoded);
        Assert.Empty(Rank(AllianceRankService.Live.Snapshot(alliance.Id), custom.Id).PrivateInformation!.Rights);
        await own.Quiet();
    }

    [Fact]
    public async Task Malformed_rights_storage_stops_entry_before_encoding_and_restores_the_callers_context()
    {
        var alliance = Create();
        using var connection = new SqliteConnection(AllianceStore.ConnectionStringOverride);
        connection.Open();
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE AllianceRanks SET Rights = '1,bad,3' WHERE AllianceId = $id AND RankId = 2;";
        update.Parameters.AddWithValue("$id", alliance.Id);
        update.ExecuteNonQuery();
        var spy = new RecordingCodec();
        await using var own = await ClientPipe.OpenAsync(8_196_107, Leader, 8_196_208, codec: spy);
        var wrong = GameSession.SinSocket();
        using var context = SessionContext.Push(wrong);
        await Assert.ThrowsAsync<FormatException>(() => AllianceHandler.OnWorldEntryAsync(own.Session));
        Assert.Same(wrong, SessionContext.Actual);
        Assert.Empty(spy.Encoded);
        await own.Quiet();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Entry_and_join_codec_failures_stop_before_membership_and_restore_context(bool joining)
    {
        var alliance = Create();
        AllianceStore.Join(Member, alliance.Id);
        var failure = new GameConversionException("alliance rank encoding failed");
        var spy = new RecordingCodec(failure);
        await using var pipe = await ClientPipe.OpenAsync(8_196_109, Member, 8_196_212, codec: spy);
        var wrong = GameSession.SinSocket();
        using var context = SessionContext.Push(wrong);
        using var turn = await pipe.Session.EnterTurnAsync();
        Assert.Same(failure, await Assert.ThrowsAsync<GameConversionException>(() => joining
            ? AllianceHandler.SendJoinedAsync(pipe.Session, alliance)
            : AllianceHandler.OnWorldEntryAsync(pipe.Session)));
        Assert.Same(wrong, SessionContext.Actual);
        Assert.IsType<AllianceRanksEvent>(Assert.Single(spy.Encoded).Content);
        await pipe.Quiet();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Application_and_invitation_acceptance_use_the_joining_recipients_codec_and_restore_context(bool invitation)
    {
        if (!File.Exists(Paths.WorldDb)) return;
        long account = invitation ? 8_196_504 : 8_196_503;
        long who = Character(account, invitation ? "Rank-Invited" : "Rank-Accepted");
        try
        {
            var alliance = Create();
            var custom = AllianceStore.CreateRank(alliance.Id, "Gardien", 91_001, 2);
            var newcomer = AllianceStore.Ranks(alliance.Id).Single(rank => rank.Id == AllianceStore.RankNewcomer);
            newcomer.Name = "Recrue";
            AllianceStore.SaveRank(newcomer);
            var ownCodec = new RecordingCodec();
            var joiningCodec = new RecordingCodec();
            await using var own = await ClientPipe.OpenAsync(8_196_108, Leader, 8_196_209, "Rank-Leader", ownCodec);
            await using var joining = await ClientPipe.OpenAsync(account, who, 8_196_210, codec: joiningCodec);
            using var context = SessionContext.Push(own.Session);
            using var ownTurn = await own.Session.EnterTurnAsync();
            using var joiningTurn = await joining.Session.EnterTurnAsync();
            if (invitation)
            {
                await AllianceHandler.InviteAsync(own.ToClient, ConnectionProtocol.Push(Op.Mbr, Pb.New().Var(1, who).Build()));
                Assert.NotNull(ConnectionProtocol.ReadPayload(await Packet(own), Op.Mbq));
                Assert.NotNull(ConnectionProtocol.ReadPayload(await Packet(joining), Op.Mbs));
                using (SessionContext.Push(joining.Session))
                    await AllianceHandler.AnswerInvitationAsync(joining.ToClient, ConnectionProtocol.Push(Op.Mbu, Pb.New().Var(1, 1).Build())).WaitAsync(TimeSpan.FromSeconds(5));
            }
            else
            {
                AllianceStore.Apply(who, alliance.Id, "Ready");
                await AllianceHandler.AnswerApplicationAsync(own.ToClient, ConnectionProtocol.Push(Op.Maf,
                    Pb.New().Var(1, 1).Var(2, who).Build())).WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.Same(own.Session, SessionContext.Actual);
            var snapshot = await UntilRanks(joining);
            Assert.Equal("Recrue", Rank(snapshot, AllianceStore.RankNewcomer).Name);
            Assert.Equal("Gardien", Rank(snapshot, custom.Id).Name);
            Assert.Equal(ConnectionProtocol.Push(Op.Mbz, AllianceProtocol.BuildMembership(alliance, AllianceStore.RankNewcomer)), await Packet(joining));
            Assert.Empty(ownCodec.Encoded);
            Assert.Collection(joiningCodec.Encoded,
                frame => Assert.IsType<AllianceRanksEvent>(frame.Content),
                frame => Assert.IsType<AllianceMotdEvent>(frame.Content),
                frame => Assert.IsType<AllianceBulletinEvent>(frame.Content));
            Assert.Equal(alliance.Id, AllianceStore.AllianceOf(who)!.Id);
        }
        finally
        {
            try { await AllianceHandler.DisconnectedAsync(who); }
            finally { DatabaseManager.DeleteCharacter(who, account); }
        }
    }

    [Fact]
    public async Task Founding_sends_catalogue_ranks_after_creation_success_and_spends_the_gem_once()
    {
        if (!File.Exists(Paths.WorldDb)) return;
        const long account = 8_196_505;
        long who = Character(account, "Rank-Founder");
        try
        {
            var spy = new RecordingCodec();
            await using var pipe = await ClientPipe.OpenAsync(account, who, 8_196_211, codec: spy);
            pipe.Session.State.CharacterLevel = 200;
            using var context = SessionContext.Push(pipe.Session);
            Assert.True(await Equipment.GiveAsync(pipe.ToClient, AllianceHandler.AllianceGemTemplate, 1));
            Assert.Equal(AllianceProtocol.CreationOk, await AllianceHandler.FoundAsync(pipe.ToClient, who,
                "Rank founders", "RNK", 101, 202, 0x123456, 0, 0xabcdef));
            var prefix = new List<byte[]>();
            var snapshot = await UntilRanks(pipe, prefix);
            Assert.Contains(prefix, packet => packet.AsSpan().SequenceEqual(ConnectionProtocol.Push(Op.Mch,
                AllianceProtocol.BuildCreationResult(AllianceProtocol.CreationOk))));
            Assert.Equal(new[] { 1, 2, 3, 4 }, snapshot.Ranks.Select(rank => rank.Id));
            Assert.Equal(0, Equipment.HowMany(AllianceHandler.AllianceGemTemplate));
            Assert.Single(spy.Encoded, frame => frame.Content is AllianceRanksEvent);
        }
        finally { DatabaseManager.DeleteCharacter(who, account); }
    }

    private static long Character(long account, string name) => DatabaseManager.CreateCharacter(account, 1, name, 4, 0, 1,
        new long[] { -1, -1, -1, -1, -1 }, 191105026, 200, 0, 0, Array.Empty<(int, int)>());
    private static RankInformation Rank(AllianceRanksEvent snapshot, int id) => Assert.Single(snapshot.Ranks, rank => rank.Id == id);
    private static Task<byte[]> Packet(ClientPipe pipe) => Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer).WaitAsync(TimeSpan.FromSeconds(5));
    private static async Task<AllianceRanksEvent> NextRanks(ClientPipe pipe)
    {
        var frame = Codec.Decode(await Packet(pipe));
        Assert.Null(frame.Uid);
        return Assert.IsType<AllianceRanksEvent>(frame.Content);
    }
    private static async Task<AllianceRanksEvent> UntilRanks(ClientPipe pipe, List<byte[]>? prefix = null)
    {
        for (int i = 0; i < 64; i++)
        {
            byte[] packet = await Packet(pipe);
            if (GameBindings.StableName(Codec.ReadEnvelope(packet).Alias) == nameof(AllianceRanksEvent))
                return Assert.IsType<AllianceRanksEvent>(Codec.Decode(packet).Content);
            prefix?.Add(packet);
        }
        throw new Xunit.Sdk.XunitException("Alliance rank snapshot did not arrive.");
    }
    private sealed class RecordingCodec(Exception? failure = null) : IGameCodec
    {
        internal readonly List<GameFrame> Encoded = [];
        public byte[] Encode(GameFrame frame)
        {
            Encoded.Add(frame);
            if (failure != null) throw failure;
            return Codec.Encode(frame);
        }
        public GameFrame Decode(ReadOnlySpan<byte> frame) => throw new InvalidOperationException("No decoding expected.");
    }
}
