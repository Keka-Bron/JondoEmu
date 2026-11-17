using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class FriendListTests
{
    [Fact]
    public async Task Owner_refresh_keeps_mutual_then_contacts_order_empty_replacements_and_detached_account_state()
    {
        var backend = new Backend();
        var codec = new RecordingCodec();
        var owner = Recipient(71_001, codec);
        var other = Recipient(71_099, new());
        var playing = Recipient(71_002, new());
        playing.State.CharacterId = 300_000_000_002;
        playing.State.CharacterName = "Playing";
        playing.State.CharacterLevel = 357;
        playing.State.Breed = 8;
        playing.State.Sex = 1;
        playing.State.ChatStatus = 2;
        playing.State.ChatStatusMessage = "later";
        playing.State.IsInFight = true;
        backend.Lists[71_001] = [71_003, 71_004, 71_002];
        backend.Lists[71_002] = [71_001];
        backend.Lists[71_003] = [71_001];
        backend.Sessions[71_002] = playing;
        var service = new FriendListService(backend);
        using var context = SessionContext.Push(other);
        using var turn = await owner.EnterTurnAsync();
        await service.RefreshAsync(owner).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Collection(codec.Frames,
            frame => Assert.IsType<FriendListEvent>(frame.Content),
            frame => Assert.IsType<AcquaintanceListEvent>(frame.Content));
        var mutual = Assert.IsType<FriendListEvent>(codec.Frames[0].Content);
        Assert.Equal(new long[] { 71_003, 71_002 }, mutual.Friends.Select(friend => friend.AccountId));
        Assert.Null(mutual.Friends[0].OnlineInformation);
        Assert.Equal(8_000_000_003, mutual.Friends[0].DurationSinceLastConnectionHours);
        Assert.Equal(CharacterState.NotConnected, mutual.Friends[0].State);
        var online = mutual.Friends[1];
        Assert.Equal(-1, online.LeagueId);
        Assert.Equal(CharacterState.InFight, online.State);
        Assert.Equal(new FriendOnlineInformation(new(CharacterStatusStatus.Idle, "later"), 0, null,
            357, null, 0, 8, 0, 300_000_000_002, true, Gender.Female, "Playing"), online.OnlineInformation);
        Assert.Equal(new AccountTag("Account71002", "0042"), online.AccountTag);
        Assert.Equal(new AcquaintanceInformation(null, 71_004, new("Account71004", "0042"), CharacterState.UnknownState),
            Assert.Single(Assert.IsType<AcquaintanceListEvent>(codec.Frames[1].Content).Acquaintances));
        playing.State.CharacterName = "changed";
        playing.State.ChatStatusMessage = "changed";
        backend.Lists[71_001].Clear();
        Assert.Equal("Playing", online.OnlineInformation!.CharacterName);
        Assert.Equal("later", online.OnlineInformation.Status!.Message);
        await service.RefreshAsync(owner);
        Assert.Empty(Assert.IsType<FriendListEvent>(codec.Frames[2].Content).Friends);
        Assert.Empty(Assert.IsType<AcquaintanceListEvent>(codec.Frames[3].Content).Acquaintances);
        Assert.Same(other, SessionContext.Actual);
        Assert.Equal(0, owner.UnoCadaVez.CurrentCount);
        Assert.All(codec.Frames, frame => Assert.Null(frame.Uid));
        Assert.Equal(0, codec.Decodes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Own_encode_or_transport_failure_stops_before_contacts(bool encode)
    {
        var failure = new IOException("friend delivery");
        var codec = new RecordingCodec { Failure = encode ? failure : null };
        int sends = 0;
        var owner = new GameSession(codec, _ => { sends++; return Task.FromException(failure); });
        owner.BindAccount(71_001, 1);
        var backend = new Backend();
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => new FriendListService(backend).RefreshAsync(owner)));
        Assert.Single(codec.Frames);
        Assert.Equal(encode ? 0 : 1, sends);
        Assert.Equal(new long[] { 71_001 }, backend.Reads);
    }

    [Fact]
    public async Task Source_failure_propagates_before_any_owner_delivery()
    {
        var backend = new Backend { Fail = true };
        var codec = new RecordingCodec();
        var owner = Recipient(71_001, codec);
        await Assert.ThrowsAsync<IOException>(() => new FriendListService(backend).SendMutualAsync(owner));
        Assert.Empty(codec.Frames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Presence_uses_each_mutual_recipients_codec_and_continues_after_recipient_failure(bool encode)
    {
        var backend = new Backend();
        backend.Lists[71_001] = [71_004, 71_002, 71_003, 71_005];
        backend.Lists[71_002] = [71_001];
        backend.Lists[71_003] = [71_001];
        backend.Lists[71_005] = [71_001]; // offline, skipped
        var failedCodec = new RecordingCodec { Failure = encode ? new IOException("encoding") : null };
        backend.Sessions[71_002] = Recipient(71_002, failedCodec, _ => throw new IOException("transport"));
        var goodCodec = new RecordingCodec();
        var good = Recipient(71_003, goodCodec);
        backend.Sessions[71_003] = good;
        var oneWayCodec = new RecordingCodec();
        backend.Sessions[71_004] = Recipient(71_004, oneWayCodec);
        using var recipientTurn = await good.EnterTurnAsync(); // fanout must not acquire it
        await new FriendListService(backend).AnnouncePresenceAsync(71_001).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(failedCodec.Frames);
        var list = Assert.IsType<FriendListEvent>(Assert.Single(goodCodec.Frames).Content);
        Assert.Equal(71_001, Assert.Single(list.Friends).AccountId);
        Assert.Empty(oneWayCodec.Frames);
    }

    [Fact]
    public async Task Failed_remote_refresh_stops_before_second_list_and_is_isolated()
    {
        var codec = new RecordingCodec { Failure = new IOException("remote") };
        var backend = new Backend();
        backend.Sessions[71_002] = Recipient(71_002, codec);
        await new FriendListService(backend).RefreshOfAsync(71_002);
        Assert.IsType<FriendListEvent>(Assert.Single(codec.Frames).Content);
    }

    [Fact]
    public async Task Typed_community_dispatch_uses_explicit_owner_codec_without_echoing_the_scalar()
    {
        var codec = new RecordingCodec();
        var owner = Recipient(71_001, codec);
        var ambientCodec = new RecordingCodec();
        var ambient = Recipient(71_099, ambientCodec);
        var entry = new GameEntryService(owner, new GameEntryBackend());
        using var context = SessionContext.Push(ambient);
        using var turn = await owner.EnterTurnAsync();
        Assert.Equal(GameEntryResult.Handled, await GameNodeProxy.DispatchTypedAsync(owner,
            new(new ChatCommunitySelectionRequest(2), -1), entry));
        var reply = Assert.Single(codec.Frames);
        Assert.IsType<CommunityChannelEvent>(reply.Content);
        Assert.Null(reply.Uid);
        Assert.Empty(ambientCodec.Frames);
        Assert.Same(ambient, SessionContext.Actual);
        Assert.Equal(0, codec.Decodes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Community_delivery_failure_propagates_without_retry(bool encode)
    {
        var failure = new IOException("community delivery");
        var codec = new RecordingCodec { Failure = encode ? failure : null };
        int sends = 0;
        var owner = new GameSession(codec, _ => { sends++; throw failure; });
        var entry = new GameEntryService(owner, new GameEntryBackend());
        await Assert.ThrowsAsync<IOException>(() => GameNodeProxy.DispatchTypedAsync(owner,
            new(new ChatCommunitySelectionRequest(2), 0), entry));
        Assert.Single(codec.Frames);
        Assert.Equal(encode ? 0 : 1, sends);
    }

    internal sealed class RecordingCodec : IGameCodec
    {
        internal readonly List<GameFrame> Frames = [];
        internal Exception? Failure;
        internal int Decodes;
        public byte[] Encode(GameFrame frame) { Frames.Add(frame); if (Failure != null) throw Failure; return [0x7a, checked((byte)Frames.Count)]; }
        public GameFrame Decode(ReadOnlySpan<byte> frame) { Decodes++; throw new InvalidOperationException("No wire projection expected."); }
    }

    private static GameSession Recipient(long account, RecordingCodec codec, Func<byte[], Task>? send = null)
    {
        var session = new GameSession(codec, send ?? (_ => Task.CompletedTask));
        session.BindAccount(account, 1);
        return session;
    }

    private sealed class Backend : IFriendListBackend
    {
        internal bool Fail;
        internal readonly Dictionary<long, List<long>> Lists = [];
        internal readonly Dictionary<long, GameSession> Sessions = [];
        internal readonly List<long> Reads = [];
        public IReadOnlyList<long> Contacts(long account) { Reads.Add(account); if (Fail) throw new IOException("contacts source"); return Lists.GetValueOrDefault(account) ?? []; }
        public bool IsContact(long account, long other) => Lists.TryGetValue(account, out var list) && list.Contains(other);
        public GameSession? Playing(long account) => Sessions.GetValueOrDefault(account);
        public AccountTag Tag(long account) => new($"Account{account}", "0042");
        public long HoursOffline(long account) => 8_000_000_003;
        public bool SharesHavenBag(long character) => character == 300_000_000_002;
    }
}
