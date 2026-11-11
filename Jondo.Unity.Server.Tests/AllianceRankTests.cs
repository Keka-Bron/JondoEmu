using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class AllianceRankTests
{
    private static readonly GameCodec Wire = new();

    [Fact]
    public void Snapshot_preserves_custom_values_order_duplicates_unknown_and_full_int32_rights_and_detaches_sources()
    {
        var rank = new AllianceStore.Rank { Id = 81_005, Name = "Gardien", Flag = true, Icon = 91_001, Order = 7,
            Rights = [257, 128, 257, 0, int.MaxValue, int.MinValue, -1] };
        var source = new List<AllianceStore.Rank> { rank, new() { Id = 81_002, Name = "alliance.rank.2.name" } };
        var snapshot = AllianceRankMessages.Snapshot(source);
        rank.Name = "changed";
        rank.Flag = false;
        rank.Icon = 2;
        rank.Rights.Clear();
        source.Clear();
        Assert.Collection(snapshot.Ranks,
            actual =>
            {
                Assert.Equal(81_005, actual.Id);
                Assert.Equal("Gardien", actual.Name);
                Assert.Equal(new RankInformationOrderAndGfx(91_001, 7), actual.OrderAndGfx);
                Assert.True(actual.PrivateInformation!.Modifiable);
                Assert.Equal(new[] { 257, 128, 257, 0, int.MaxValue, int.MinValue, -1 }, actual.PrivateInformation.Rights);
            },
            actual =>
            {
                Assert.Equal(81_002, actual.Id);
                Assert.Equal("alliance.rank.2.name", actual.Name);
                Assert.Equal(new RankInformationOrderAndGfx(0, 0), actual.OrderAndGfx);
                Assert.False(actual.PrivateInformation!.Modifiable);
                Assert.Empty(actual.PrivateInformation.Rights);
            });
    }

    [Fact]
    public async Task Empty_snapshot_is_sent_through_the_owning_codec_under_another_context_and_an_existing_turn()
    {
        var target = Recipient();
        var other = Recipient();
        var backend = new Backend();
        using (SessionContext.Push(other.Session))
        using (await target.Session.EnterTurnAsync())
        {
            await new AllianceRankService(backend).SendAsync(target.Session, 91_001).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(other.Session, SessionContext.Actual);
        }
        Assert.Empty(Snapshot(Assert.Single(target.Packets)).Ranks);
        Assert.Equal(1, target.Codec.Encodes);
        Assert.Equal(0, target.Codec.Decodes);
        Assert.Empty(other.Packets);
        Assert.Equal(0, other.Codec.Encodes);
        Assert.Equal(new[] { "ranks:91001" }, backend.Calls);
    }

    [Fact]
    public async Task Repeated_delivery_reloads_the_requested_alliances_current_ranks()
    {
        var target = Recipient();
        var backend = new Backend();
        backend.Sources[91_001] = [new() { Id = 81_005, Name = "Before" }];
        backend.Sources[91_002] = [new() { Id = 82_005, Name = "Other alliance" }];
        var service = new AllianceRankService(backend);
        await service.SendAsync(target.Session, 91_001);
        backend.Sources[91_001][0].Name = "After";
        await service.SendAsync(target.Session, 91_001);
        await service.SendAsync(target.Session, 91_002);
        Assert.Equal(new[] { "Before", "After", "Other alliance" }, target.Packets.Select(packet => Assert.Single(Snapshot(packet).Ranks).Name));
        Assert.Equal(3, target.Codec.Encodes);
        Assert.Equal(0, target.Codec.Decodes);
    }

    [Fact]
    public async Task Broadcast_captures_ranks_and_members_before_awaiting_and_encodes_for_each_online_recipient()
    {
        var backend = new Backend();
        var source = new AllianceStore.Rank { Id = 81_005, Name = "Gardien", Rights = [128, 257] };
        backend.Sources[91_001] = [source];
        var first = Recipient(_ => { source.Name = "changed"; source.Rights.Clear(); backend.People.Clear(); return Task.CompletedTask; });
        var second = Recipient();
        var other = Recipient();
        backend.People.AddRange([new() { CharacterId = 71_001 }, new() { CharacterId = 71_002 }, new() { CharacterId = 71_003 }]);
        backend.Sessions[71_001] = first.Session;
        backend.Sessions[71_003] = second.Session;
        using (SessionContext.Push(other.Session))
        using (await first.Session.EnterTurnAsync())
            Assert.Equal(2, await new AllianceRankService(backend).BroadcastAsync(91_001).WaitAsync(TimeSpan.FromSeconds(5)));
        foreach (var target in new[] { first, second })
        {
            var actual = Assert.Single(Snapshot(Assert.Single(target.Packets)).Ranks);
            Assert.Equal("Gardien", actual.Name);
            Assert.Equal(new[] { 128, 257 }, actual.PrivateInformation!.Rights);
            Assert.Equal(1, target.Codec.Encodes);
            Assert.Equal(0, target.Codec.Decodes);
        }
        Assert.Empty(other.Packets);
        Assert.Equal(new[] { "ranks:91001", "members:91001", "session:71001", "session:71002", "session:71003" }, backend.Calls);
    }

    [Fact]
    public async Task Broadcast_without_online_members_returns_zero()
    {
        var backend = new Backend();
        backend.People.Add(new() { CharacterId = 71_001 });
        Assert.Equal(0, await new AllianceRankService(backend).BroadcastAsync(91_001));
    }

    [Theory]
    [InlineData("ranks:91001")]
    [InlineData("members:91001")]
    public async Task Broadcast_source_failure_propagates_before_any_recipient_is_encoded(string failAt)
    {
        var failure = new InvalidOperationException("alliance rank storage failed");
        var backend = new Backend { FailAt = failAt, Failure = failure };
        var target = Recipient();
        backend.People.Add(new() { CharacterId = 71_001 });
        backend.Sessions[71_001] = target.Session;
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => new AllianceRankService(backend).BroadcastAsync(91_001)));
        Assert.Equal(1, backend.Calls.Count(call => call == failAt));
        Assert.Empty(target.Packets);
        Assert.Equal(0, target.Codec.Encodes);
    }

    [Fact]
    public async Task Single_recipient_source_failure_is_not_reclassified_as_codec_or_transport_failure()
    {
        var failure = new FormatException("alliance rights storage failed");
        var backend = new Backend { FailAt = "ranks:91001", Failure = failure };
        var target = Recipient();
        Assert.Same(failure, await Assert.ThrowsAsync<FormatException>(() => new AllianceRankService(backend).SendAsync(target.Session, 91_001)));
        Assert.Empty(target.Packets);
        Assert.Equal(0, target.Codec.Encodes);
        Assert.Equal(new[] { "ranks:91001" }, backend.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Broadcast_codec_or_transport_failure_stops_later_recipients_without_retries(bool codecFailure)
    {
        Exception failure = codecFailure ? new GameConversionException("alliance codec failed") : new IOException("alliance transport failed");
        int sends = 0;
        var first = new GameSession(codecFailure ? new FailingCodec(failure) : Wire, _ => { sends++; return Task.FromException(failure); });
        var second = Recipient();
        var backend = new Backend();
        backend.People.AddRange([new() { CharacterId = 71_001 }, new() { CharacterId = 71_002 }]);
        backend.Sessions[71_001] = first;
        backend.Sessions[71_002] = second.Session;
        Assert.Same(failure, await Record.ExceptionAsync(() => new AllianceRankService(backend).BroadcastAsync(91_001)));
        Assert.Equal(codecFailure ? 0 : 1, sends);
        Assert.Empty(second.Packets);
        Assert.Equal(0, second.Codec.Encodes);
        Assert.DoesNotContain("session:71002", backend.Calls);
    }

    private static AllianceRanksEvent Snapshot(byte[] packet)
    {
        var frame = Wire.Decode(packet);
        Assert.Null(frame.Uid);
        return Assert.IsType<AllianceRanksEvent>(frame.Content);
    }
    private static (GameSession Session, List<byte[]> Packets, GameMessagingTests.CountingCodec Codec) Recipient(Func<byte[], Task>? after = null)
    {
        var packets = new List<byte[]>();
        var codec = new GameMessagingTests.CountingCodec();
        return (new GameSession(codec, packet => { packets.Add(packet); return after?.Invoke(packet) ?? Task.CompletedTask; }), packets, codec);
    }
    private sealed class Backend : IAllianceRankBackend
    {
        internal readonly Dictionary<long, IReadOnlyList<AllianceStore.Rank>> Sources = [];
        internal readonly List<AllianceStore.Member> People = [];
        internal readonly Dictionary<long, GameSession> Sessions = [];
        internal readonly List<string> Calls = [];
        internal string FailAt = "";
        internal Exception? Failure;
        private void Call(string name) { Calls.Add(name); if (name == FailAt) throw Failure!; }
        public IReadOnlyList<AllianceStore.Rank> Ranks(long allianceId) { Call($"ranks:{allianceId}"); return Sources.GetValueOrDefault(allianceId) ?? []; }
        public IReadOnlyList<AllianceStore.Member> Members(long allianceId) { Call($"members:{allianceId}"); return People; }
        public GameSession? FindSession(long characterId) { Call($"session:{characterId}"); return Sessions.GetValueOrDefault(characterId); }
    }
    private sealed class FailingCodec(Exception failure) : IGameCodec
    {
        public byte[] Encode(GameFrame frame) => throw failure;
        public GameFrame Decode(ReadOnlySpan<byte> frame) => throw new InvalidOperationException("No decoding expected.");
    }
}
