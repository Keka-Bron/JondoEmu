using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World;

[Collection("alliances")]
public sealed class AllianceNoticeFlowTests : IDisposable
{
    private static readonly GameCodec Wire = new();
    private const long Leader = 8_197_001, Member = 8_197_002;
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"jondo-alliance-notices-{Guid.NewGuid():N}.db");
    public AllianceNoticeFlowTests() => AllianceStore.ConnectionStringOverride = $"Data Source={_file}";
    public void Dispose()
    {
        AllianceStore.ConnectionStringOverride = null;
        TestDatabases.ReleasePool(_file);
        File.Delete(_file);
    }
    private static AllianceStore.Alliance Create()
    {
        var alliance = AllianceStore.Create(Leader, "Notice sources", "NTS", 101, 202, 0x123456, 0, 0xabcdef);
        AllianceStore.Join(Member, alliance.Id);
        return alliance;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Entry_and_join_send_current_notices_in_order_through_the_explicit_owner(bool joining)
    {
        var alliance = Create();
        AllianceStore.SetNotice(alliance.Id, AllianceStore.NoticeMessage, new("Éclaireurs ☀", 4_000_000_001, "Writer", 9_000_000_001));
        var codec = new RecordingCodec();
        await using var own = await ClientPipe.OpenAsync(8_197_101, Member, 8_197_201, codec: codec);
        var other = GameSession.SinSocket();
        using var context = SessionContext.Push(other);
        using var turn = await own.Session.EnterTurnAsync();
        if (joining) await AllianceHandler.SendJoinedAsync(own.Session, alliance).WaitAsync(TimeSpan.FromSeconds(5));
        else await AllianceHandler.OnWorldEntryAsync(own.Session).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(other, SessionContext.Actual);
        var first = await UntilNotice(own);
        Assert.Equal(new SocialNoticeInformation("Éclaireurs ☀", 4_000_000_001, "Writer", 9_000_000_001),
            Assert.IsType<AllianceMotdEvent>(first).MotdInfo);
        Assert.Equal(new SocialNoticeInformation("", alliance.CreatedUtcS, "", 0),
            Assert.IsType<AllianceBulletinEvent>((await Frame(own)).Content).BulletinInfo);
        Assert.Collection(codec.Frames,
            frame => Assert.IsType<AllianceRanksEvent>(frame.Content),
            frame => Assert.IsType<AllianceMotdEvent>(frame.Content),
            frame => Assert.IsType<AllianceBulletinEvent>(frame.Content));
        using var connection = Open();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM AllianceNotices;";
        Assert.Equal(1L, count.ExecuteScalar()); // Default bulletin was not persisted.
        await own.Quiet();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Edits_and_clearing_are_saved_before_recipient_specific_broadcasts(bool bulletin)
    {
        var alliance = Create();
        var ownCodec = new RecordingCodec();
        var memberCodec = new RecordingCodec();
        await using var own = await ClientPipe.OpenAsync(8_197_102, Leader, 8_197_202, "Notice-Writer", ownCodec);
        await using var member = await ClientPipe.OpenAsync(8_197_103, Member, 8_197_203, codec: memberCodec);
        using var context = SessionContext.Push(own.Session);
        using var turn = await own.Session.EnterTurnAsync();
        foreach (string text in new[] { "Réunion ☀\nà midi", "" })
        {
            long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await Edit(own, bulletin, text);
            var saved = AllianceStore.NoticeOf(alliance, bulletin ? 1 : 0);
            Assert.Equal(text, saved.Text);
            Assert.Equal("Notice-Writer", saved.AuthorName);
            Assert.Equal(Leader, saved.AuthorId);
            Assert.InRange(saved.WhenS, before, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            foreach (var pipe in new[] { own, member })
            {
                var frame = await Frame(pipe);
                Assert.Null(frame.Uid);
                var information = bulletin ? Assert.IsType<AllianceBulletinEvent>(frame.Content).BulletinInfo
                    : Assert.IsType<AllianceMotdEvent>(frame.Content).MotdInfo;
                Assert.Equal(new SocialNoticeInformation(text, saved.WhenS, "Notice-Writer", Leader), information);
            }
        }
        Assert.Equal(2, ownCodec.Frames.Count);
        Assert.Equal(2, memberCodec.Frames.Count);
        await own.Quiet();
        await member.Quiet();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_right_refuses_without_a_write_or_typed_broadcast(bool bulletin)
    {
        var alliance = Create();
        var codec = new RecordingCodec();
        await using var member = await ClientPipe.OpenAsync(8_197_104, Member, 8_197_204, codec: codec);
        using var context = SessionContext.Push(member.Session);
        await Edit(member, bulletin, "Forbidden");
        Assert.NotNull(ConnectionProtocol.ReadPayload(await Packet(member), bulletin ? Op.Mco : Op.Mdf));
        Assert.Empty(codec.Frames);
        Assert.Equal("", AllianceStore.NoticeOf(alliance, bulletin ? 1 : 0).Text);
        await member.Quiet();
    }

    [Fact]
    public async Task Failed_write_leaves_the_previous_notice_and_sends_no_update()
    {
        var alliance = Create();
        AllianceStore.SetNotice(alliance.Id, 0, new("Before", 123, "Original", Leader));
        using (var connection = Open())
        using (var trigger = connection.CreateCommand())
        {
            trigger.CommandText = "CREATE TRIGGER FailNotice BEFORE INSERT ON AllianceNotices BEGIN SELECT RAISE(ABORT, 'test notice write failure'); END;";
            trigger.ExecuteNonQuery();
        }
        var codec = new RecordingCodec();
        await using var own = await ClientPipe.OpenAsync(8_197_105, Leader, 8_197_205, codec: codec);
        using var context = SessionContext.Push(own.Session);
        await Assert.ThrowsAsync<SqliteException>(() => Edit(own, false, "After"));
        Assert.Equal("Before", AllianceStore.NoticeOf(alliance, 0).Text);
        Assert.Empty(codec.Frames);
        await own.Quiet();
    }

    [Fact]
    public async Task Failed_delivery_keeps_the_saved_notice_and_stops_before_later_recipients()
    {
        var alliance = Create();
        var failure = new GameConversionException("test notice codec failure");
        var codec = new RecordingCodec { FailType = typeof(AllianceMotdEvent), Failure = failure };
        await using var own = await ClientPipe.OpenAsync(8_197_106, Leader, 8_197_206, "Writer", codec);
        await using var member = await ClientPipe.OpenAsync(8_197_107, Member, 8_197_207);
        using var context = SessionContext.Push(own.Session);
        Assert.Same(failure, await Assert.ThrowsAsync<GameConversionException>(() => Edit(own, false, "Saved")));
        Assert.Equal("Saved", AllianceStore.NoticeOf(alliance, 0).Text);
        Assert.Single(codec.Frames);
        await own.Quiet();
        await member.Quiet();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bulletin_encoding_failure_keeps_the_sent_motd_and_restores_entry_or_join_context(bool joining)
    {
        var alliance = Create();
        var failure = new GameConversionException("test bulletin failure");
        var codec = new RecordingCodec { FailType = typeof(AllianceBulletinEvent), Failure = failure };
        await using var own = await ClientPipe.OpenAsync(8_197_108, Member, 8_197_208, codec: codec);
        var other = GameSession.SinSocket();
        using var context = SessionContext.Push(other);
        using var turn = await own.Session.EnterTurnAsync();
        Assert.Same(failure, await Assert.ThrowsAsync<GameConversionException>(() => joining
            ? AllianceHandler.SendJoinedAsync(own.Session, alliance) : AllianceHandler.OnWorldEntryAsync(own.Session)));
        Assert.IsType<AllianceMotdEvent>(await UntilNotice(own));
        Assert.Same(other, SessionContext.Actual);
        Assert.Equal(3, codec.Frames.Count);
        await own.Quiet();
    }

    private static Task Edit(ClientPipe pipe, bool bulletin, string text) => bulletin
        ? AllianceHandler.SetBulletinAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Mdg, Pb.New().Str(1, text).Build()))
        : AllianceHandler.SetMessageAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Mcs, Pb.New().Str(1, text).Build()));
    private static Task<byte[]> Packet(ClientPipe pipe) => Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer).WaitAsync(TimeSpan.FromSeconds(5));
    private static async Task<GameFrame> Frame(ClientPipe pipe) => Wire.Decode(await Packet(pipe));
    private static async Task<GamePayload> UntilNotice(ClientPipe pipe)
    {
        for (int i = 0; i < 12; i++)
        {
            var packet = await Packet(pipe);
            var name = GameBindings.StableName(Wire.ReadEnvelope(packet).Alias);
            if (name is nameof(AllianceMotdEvent) or nameof(AllianceBulletinEvent)) return Wire.Decode(packet).Content;
        }
        throw new InvalidOperationException("Notice not delivered");
    }
    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection(AllianceStore.ConnectionStringOverride);
        connection.Open();
        return connection;
    }
    private sealed class RecordingCodec : IGameCodec
    {
        internal readonly List<GameFrame> Frames = [];
        internal Type? FailType;
        internal Exception? Failure;
        public byte[] Encode(GameFrame frame)
        {
            Frames.Add(frame);
            if (frame.Content.GetType() == FailType) throw Failure!;
            return Wire.Encode(frame);
        }
        public GameFrame Decode(ReadOnlySpan<byte> packet) => Wire.Decode(packet);
    }
}
