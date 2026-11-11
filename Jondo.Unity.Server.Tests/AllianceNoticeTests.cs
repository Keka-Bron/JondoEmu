using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class AllianceNoticeTests
{
    [Theory]
    [InlineData(AllianceStore.NoticeMessage)]
    [InlineData(AllianceStore.NoticeBulletin)]
    public void Snapshots_keep_present_empty_information_and_full_width_values(int kind)
    {
        Assert.Equal(new SocialNoticeInformation("", 0, "", 0), Information(AllianceNoticeMessages.Snapshot(kind, new("", 0, "", 0))));
        Assert.Equal(new SocialNoticeInformation("Éclaireurs ☀\n", long.MaxValue, "Auteur", long.MinValue),
            Information(AllianceNoticeMessages.Snapshot(kind, new("Éclaireurs ☀\n", long.MaxValue, "Auteur", long.MinValue))));
        Assert.Throws<ArgumentOutOfRangeException>(() => AllianceNoticeMessages.Snapshot(99, new("", 0, "", 0)));
    }

    [Fact]
    public async Task Entry_uses_fresh_sources_in_order_under_the_owners_existing_turn_and_restores_context()
    {
        var backend = new Backend();
        var own = new StatusRecipient();
        var other = new StatusRecipient();
        var alliance = new AllianceStore.Alliance { Id = 91_001 };
        using (SessionContext.Push(other.Session))
        using (await own.Session.EnterTurnAsync())
        {
            await new AllianceNoticeService(backend).SendAsync(own.Session, alliance).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(other.Session, SessionContext.Actual);
        }
        Assert.Collection(own.Codec.Frames,
            frame => Assert.Equal("Message", Assert.IsType<AllianceMotdEvent>(frame.Content).MotdInfo!.Content),
            frame => Assert.Equal("Bulletin", Assert.IsType<AllianceBulletinEvent>(frame.Content).BulletinInfo!.Content));
        Assert.Equal(new[] { 0, 1 }, backend.Reads);
        Assert.Empty(other.Codec.Frames);
        Assert.Equal(0, own.Codec.Decodes);
        backend.Motd = new("Changed", 2, "Another", 71_002);
        await new AllianceNoticeService(backend).SendAsync(own.Session, alliance);
        Assert.Equal("Changed", Information(own.Codec.Frames[2].Content).Content);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Source_failure_keeps_the_original_read_send_boundary(int failKind)
    {
        var failure = new IOException("notice read failed");
        var backend = new Backend { FailKind = failKind, Failure = failure };
        var own = new StatusRecipient();
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => new AllianceNoticeService(backend)
            .SendAsync(own.Session, new() { Id = 91_001 })));
        Assert.Equal(failKind, own.Packets.Count);
        Assert.Equal(failKind, own.Codec.Frames.Count);
        Assert.Equal(failKind + 1, backend.Reads.Count);
    }

    [Fact]
    public async Task Broadcast_uses_the_saved_snapshot_and_captures_members_before_awaiting_and_skips_offline()
    {
        var backend = new Backend();
        var first = new StatusRecipient(_ => { backend.People.Clear(); return Task.CompletedTask; });
        var second = new StatusRecipient();
        backend.People.AddRange([new() { CharacterId = 71_001 }, new() { CharacterId = 71_002 }, new() { CharacterId = 71_003 }]);
        backend.Sessions[71_001] = first.Session;
        backend.Sessions[71_003] = second.Session;
        Assert.Equal(2, await new AllianceNoticeService(backend).BroadcastAsync(91_001, AllianceStore.NoticeBulletin,
            new("Saved", 4_000_000_000, "Writer", 9_000_000_000)));
        foreach (var recipient in new[] { first, second })
        {
            var frame = Assert.Single(recipient.Codec.Frames);
            Assert.Null(frame.Uid);
            Assert.Equal(new SocialNoticeInformation("Saved", 4_000_000_000, "Writer", 9_000_000_000),
                Assert.IsType<AllianceBulletinEvent>(frame.Content).BulletinInfo);
            Assert.Equal(0, recipient.Codec.Decodes);
        }
        Assert.Empty(backend.Reads); // Never re-read/rebuild an already saved update for a later recipient.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Codec_and_transport_failures_stop_entry_before_the_bulletin_without_retry(bool codecFailure)
    {
        var failure = new IOException("notice delivery failed");
        var own = new StatusRecipient(codecFailure ? null : _ => Task.FromException(failure));
        if (codecFailure) own.Codec.Failure = failure;
        var backend = new Backend();
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => new AllianceNoticeService(backend)
            .SendAsync(own.Session, new() { Id = 91_001 })));
        Assert.Equal(new[] { 0 }, backend.Reads);
        Assert.Single(own.Codec.Frames);
        Assert.Equal(codecFailure ? 0 : 1, own.Packets.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Broadcast_failure_stops_later_recipients_without_retry(bool codecFailure)
    {
        var failure = new IOException("notice delivery failed");
        var first = new StatusRecipient(codecFailure ? null : _ => Task.FromException(failure));
        if (codecFailure) first.Codec.Failure = failure;
        var second = new StatusRecipient();
        var backend = new Backend();
        backend.People.AddRange([new() { CharacterId = 71_001 }, new() { CharacterId = 71_002 }]);
        backend.Sessions[71_001] = first.Session;
        backend.Sessions[71_002] = second.Session;
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => new AllianceNoticeService(backend)
            .BroadcastAsync(91_001, 0, new("Saved", 1, "Writer", 2))));
        Assert.Single(first.Codec.Frames);
        Assert.Empty(second.Codec.Frames);
    }

    private static SocialNoticeInformation Information(GamePayload message) => message switch
    {
        AllianceMotdEvent motd => motd.MotdInfo!,
        AllianceBulletinEvent bulletin => bulletin.BulletinInfo!,
        _ => throw new InvalidOperationException("Unexpected event"),
    };

    private sealed class Backend : IAllianceNoticeBackend
    {
        internal AllianceStore.Notice Motd = new("Message", 1, "Author", 71_001);
        internal readonly List<int> Reads = [];
        internal readonly List<AllianceStore.Member> People = [];
        internal readonly Dictionary<long, GameSession> Sessions = [];
        internal int FailKind = -1;
        internal Exception? Failure;
        public AllianceStore.Notice Notice(AllianceStore.Alliance alliance, int kind)
        {
            Assert.Equal(91_001, alliance.Id);
            Reads.Add(kind);
            if (kind == FailKind) throw Failure!;
            return kind == 0 ? Motd : new("Bulletin", 2, "Author", 71_001);
        }
        public IReadOnlyList<AllianceStore.Member> Members(long allianceId) { Assert.Equal(91_001, allianceId); return People; }
        public GameSession? FindSession(long characterId) => Sessions.GetValueOrDefault(characterId);
    }
}

internal sealed class StatusRecipient
{
    internal readonly List<byte[]> Packets = [];
    internal readonly StatusCodec Codec = new();
    internal readonly GameSession Session;
    internal StatusRecipient(Func<byte[], Task>? afterSend = null)
        => Session = new(Codec, packet => { Packets.Add(packet); return afterSend?.Invoke(packet) ?? Task.CompletedTask; });
}

internal sealed class StatusCodec : IGameCodec
{
    private readonly GameCodec _wire = new();
    internal readonly List<GameFrame> Frames = [];
    internal Exception? Failure;
    internal int Decodes;
    public byte[] Encode(GameFrame frame)
    {
        Frames.Add(frame);
        if (Failure != null) throw Failure;
        return _wire.Encode(frame);
    }
    public GameFrame Decode(ReadOnlySpan<byte> packet) { Decodes++; return _wire.Decode(packet); }
}
