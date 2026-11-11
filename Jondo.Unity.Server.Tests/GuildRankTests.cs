using Google.Protobuf;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class GuildRankTests
{
    private static readonly GameCodec Wire = new();

    [Fact]
    public void Snapshot_preserves_source_order_custom_names_flags_and_full_int32_rights_and_is_detached()
    {
        var source = new GuildStore.Rank { Id = 81_003, Name = "Trésorier", Flag = true, Icon = 91_001, Order = 7,
            Rights = Packed(257, 128, 257, 0, int.MaxValue, int.MinValue, -1) };
        var ranks = new List<GuildStore.Rank> { source, new() { Id = 81_001, Name = "", Icon = 0, Order = 0 } };
        var snapshot = GuildRankMessages.Snapshot(ranks);
        source.Name = "changed";
        Array.Fill(source.Rights, (byte)0);
        ranks.Clear();
        Assert.Collection(snapshot.Ranks,
            rank =>
            {
                Assert.Equal(81_003, rank.Id);
                Assert.Equal("Trésorier", rank.Name);
                Assert.Equal(new RankInformationOrderAndGfx(91_001, 7), rank.OrderAndGfx);
                Assert.True(rank.PrivateInformation!.Modifiable);
                Assert.Equal(new[] { 257, 128, 257, 0, int.MaxValue, int.MinValue, -1 }, rank.PrivateInformation.Rights);
            },
            rank =>
            {
                Assert.Equal(81_001, rank.Id);
                Assert.Equal("", rank.Name);
                Assert.Equal(new RankInformationOrderAndGfx(0, 0), rank.OrderAndGfx);
                Assert.False(rank.PrivateInformation!.Modifiable);
                Assert.Empty(rank.PrivateInformation.Rights);
            });
        Assert.Empty(GuildRankMessages.Snapshot([]).Ranks);
        Assert.Empty(GuildRankRights.Decode(null));
    }

    [Theory]
    [InlineData(new byte[] { 8, 128 })]
    [InlineData(new byte[] { 8, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255 })]
    public void Malformed_rights_never_return_a_partial_snapshot_or_partial_chest_permissions(byte[] rights)
    {
        Assert.Throws<InvalidProtocolBufferException>(() => GuildRankRights.Decode(rights));
        Assert.Throws<InvalidProtocolBufferException>(() => GuildChests.RightsOf(rights));
        Assert.Throws<InvalidProtocolBufferException>(() => GuildRankMessages.Snapshot([new() { Rights = rights }]));
    }

    [Fact]
    public void Chest_checks_use_integer_ids_rather_than_varint_bytes()
    {
        var rights = GuildChests.RightsOf(Packed(128, 257, 128));
        Assert.Equal(new[] { 128, 257 }, rights.Order());
        Assert.DoesNotContain(1, rights);
    }

    [Fact]
    public void Initial_uses_actual_guild_or_server_defaults_and_does_not_query_a_fictitious_guild()
    {
        var backend = new Backend();
        backend.Guilds[71_001] = 91_001;
        backend.Sources[91_001] = [new() { Id = 81_005, Name = "Custom" }];
        var service = new GuildRankService(backend);
        Assert.Equal(81_005, Assert.Single(service.Initial(71_001).Ranks).Id);
        var defaults = service.Initial(71_002).Ranks;
        Assert.Equal(new[] { 1, 2, 3, 4 }, defaults.Select(rank => rank.Id));
        Assert.Equal(new[] { "guild.rank.1.name", "guild.rank.2.name", "guild.rank.3.name", "guild.rank.4.name" },
            defaults.Select(rank => rank.Name));
        Assert.Equal(new[] { 116, 115, 114, 117 }, defaults.Select(rank => rank.OrderAndGfx!.GfxId));
        Assert.Equal(new[] { false, true, true, false }, defaults.Select(rank => rank.PrivateInformation!.Modifiable));
        Assert.Equal(new[] { "guild:71001", "ranks:91001", "guild:71002" }, backend.Calls);
    }

    [Fact]
    public async Task Delivery_uses_the_recipient_codec_inside_an_existing_turn_and_refreshes_from_storage()
    {
        var backend = new Backend();
        var service = new GuildRankService(backend);
        backend.Sources[91_001] = [new() { Id = 81_005, Name = "Before" }];
        var first = Recipient();
        var other = Recipient();
        using (SessionContext.Push(other.Session))
        using (await first.Session.EnterTurnAsync())
        {
            await service.SendAsync(first.Session, 91_001).WaitAsync(TimeSpan.FromSeconds(5));
            backend.Sources[91_001][0].Name = "After";
            await service.SendAsync(first.Session, 91_001).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(other.Session, SessionContext.Actual);
        }
        Assert.Equal(new[] { "Before", "After" }, first.Packets.Select(packet =>
            Assert.Single(Assert.IsType<GuildRanksEvent>(Wire.Decode(packet).Content).Ranks).Name));
        Assert.All(first.Packets, packet => Assert.Null(Wire.ReadEnvelope(packet).Uid));
        Assert.Equal(2, first.Codec.Encodes);
        Assert.Equal(0, first.Codec.Decodes);
        Assert.Empty(other.Packets);
        Assert.Equal(0, other.Codec.Encodes);
    }

    [Fact]
    public async Task Source_failure_sends_nothing_and_is_not_retried()
    {
        var failure = new InvalidOperationException("rank storage failure");
        var backend = new Backend { Failure = failure };
        var target = Recipient();
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GuildRankService(backend).SendAsync(target.Session, 91_001)));
        Assert.Equal(new[] { "ranks:91001" }, backend.Calls);
        Assert.Empty(target.Packets);
        Assert.Equal(0, target.Codec.Encodes);
    }

    [Fact]
    public async Task Malformed_storage_fails_before_encoding_or_transport()
    {
        var backend = new Backend();
        backend.Sources[91_001] = [new() { Rights = [1, 128] }];
        var target = Recipient();
        await Assert.ThrowsAsync<InvalidProtocolBufferException>(() => new GuildRankService(backend).SendAsync(target.Session, 91_001));
        Assert.Empty(target.Packets);
        Assert.Equal(0, target.Codec.Encodes);
    }

    [Fact]
    public async Task Codec_failure_and_transport_failure_propagate_without_retries()
    {
        var backend = new Backend();
        var codecFailure = new GameConversionException("rank codec failure");
        int sends = 0;
        var session = new GameSession(new FailingCodec(codecFailure), _ => { sends++; return Task.CompletedTask; });
        Assert.Same(codecFailure, await Assert.ThrowsAsync<GameConversionException>(() => new GuildRankService(backend).SendAsync(session, 91_001)));
        Assert.Equal(0, sends);
        var transportFailure = new IOException("rank transport failure");
        session = new GameSession(Wire, _ => { sends++; return Task.FromException(transportFailure); });
        Assert.Same(transportFailure, await Assert.ThrowsAsync<IOException>(() => new GuildRankService(backend).SendAsync(session, 91_001)));
        Assert.Equal(1, sends);
        Assert.Equal(2, backend.Calls.Count);
    }

    private static (GameSession Session, List<byte[]> Packets, GameMessagingTests.CountingCodec Codec) Recipient()
    {
        var packets = new List<byte[]>();
        var codec = new GameMessagingTests.CountingCodec();
        return (new GameSession(codec, packet => { packets.Add(packet); return Task.CompletedTask; }), packets, codec);
    }

    private static byte[] Packed(params int[] ids)
    {
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, leaveOpen: true))
            foreach (int id in ids) output.WriteInt32(id);
        return stream.ToArray();
    }

    private sealed class Backend : IGuildRankBackend
    {
        internal readonly Dictionary<long, long> Guilds = [];
        internal readonly Dictionary<long, IReadOnlyList<GuildStore.Rank>> Sources = [];
        internal readonly List<string> Calls = [];
        internal Exception? Failure;
        public long? GuildOf(long characterId)
        {
            Calls.Add($"guild:{characterId}");
            return Guilds.TryGetValue(characterId, out var id) ? id : null;
        }
        public IReadOnlyList<GuildStore.Rank> Ranks(long guildId)
        {
            Calls.Add($"ranks:{guildId}");
            if (Failure != null) throw Failure;
            return Sources.GetValueOrDefault(guildId) ?? [];
        }
    }

    private sealed class FailingCodec(Exception failure) : IGameCodec
    {
        public byte[] Encode(GameFrame frame) => throw failure;
        public GameFrame Decode(ReadOnlySpan<byte> frame) => throw new InvalidOperationException("No decoding expected.");
    }
}
